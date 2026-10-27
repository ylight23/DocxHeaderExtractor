using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.App;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Selectors;

if (args.Length != 4) throw new ArgumentException("Usage: <pdf> <artifact-root> <identity.json> <reply-map-by-request-sha256.json>");
var pdf = Path.GetFullPath(args[0]); var root = Path.GetFullPath(args[1]); var identity = Path.GetFullPath(args[2]);
var replayMapPath = Path.GetFullPath(args[3]);
var replayMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(replayMapPath))
  ?? throw new InvalidOperationException("REPLAY_MAP_INVALID");
Directory.CreateDirectory(Path.Combine(root, "incoming")); Directory.CreateDirectory(Path.Combine(root, "responses"));
var bridge = new LunaFileBridgeHandler(Path.Combine(root, "incoming"), Path.Combine(root, "responses"), replayMap);
var provider = new LunaBridgeProvider(bridge);
var options = new PipelineOptions { SourceCacheRoot = Path.Combine(root, "source-cache"), MaxProviderCostUsd = 5, MaxProviderAttempts = 2000 };
var replies = new ConcurrentQueue<object>();
var runner = new DocumentRunner(options, provider, r => replies.Enqueue(r));
var start = DateTimeOffset.UtcNow; var timer = System.Diagnostics.Stopwatch.StartNew();
var result = await runner.ExtractAsync(pdf);
timer.Stop(); var finish = DateTimeOffset.UtcNow;
var audit = result.HeadingPipeline?.Audit ?? result.Outline.RouteAudit;
var artifact = new { runId = Guid.NewGuid().ToString("D"), startedAtUtc=start, completedAtUtc=finish, elapsedSeconds=timer.Elapsed.TotalSeconds,
  llmProvider="Luna in-session model reasoning; local file bridge; no network inference", externalProviderCalls=0, sentDataExternally=false,
  usageAccounting="SIMULATED: synthetic SSE envelope used only to exercise production parser/audit; not provider-native usage or billing",
  buildIdentity=JsonDocument.Parse(File.ReadAllText(identity)).RootElement.Clone(), outline=result.Outline, pipelineAudit=audit,
  placementExecution=audit?.PlacementExecution, groupingObservation=audit?.Grouping, resources=result.ResourceBudget,
  replies=replies.ToArray(), bridgeRequests=bridge.RequestCount };
File.WriteAllText(Path.Combine(root,"run-result.json"), JsonSerializer.Serialize(artifact,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new { completed=finish, elapsedSeconds=timer.Elapsed.TotalSeconds, headings=result.Outline.Headings.Count, route=result.Outline.DeterministicRoute, bridgeRequests=bridge.RequestCount, result=Path.Combine(root,"run-result.json") }));

sealed class LunaBridgeProvider(LunaFileBridgeHandler bridge) : IModelProvider {
 public ModelRoute CreateRoute() => new("qwen/qwen3.7-flash", "local-luna-bridge-only");
 public T? Create<T>(string role) where T:class {
  object? value=null;
  if(role==AgentRoles.PdfSelector) value=new PdfSelector(()=>new LunaFileBridgeHandler(bridge.Incoming,bridge.Responses,bridge.ReplayMap), parallelism:1);
  else if(role==AgentRoles.Placer) {
   var t=typeof(PdfSelector).Assembly.GetType("DocxHeaderExtractor.HeadingAgent.Placement.HeadingPlacer",true)!;
   var ctor=t.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(HttpMessageHandler)},null)!;
   value=ctor.Invoke(new object[]{new LunaFileBridgeHandler(bridge.Incoming,bridge.Responses,bridge.ReplayMap)});
  }
  return value as T;
 }
}

sealed class LunaFileBridgeHandler(string incoming,string responses,Dictionary<string,string> replayMap) : HttpMessageHandler {
 static int serial; public int RequestCount=>Volatile.Read(ref serial); public string Incoming=>incoming; public string Responses=>responses; public Dictionary<string,string> ReplayMap=>replayMap;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct) {
  if(req.Method!=HttpMethod.Post || req.RequestUri?.Host!="openrouter.ai" || req.RequestUri.AbsolutePath!="/api/v1/chat/completions") throw new HttpRequestException("LOCAL_BRIDGE_REFUSED_NON_EXPECTED_DESTINATION");
  var body=await req.Content!.ReadAsByteArrayAsync(ct); var sha=Convert.ToHexStringLower(SHA256.HashData(body));
  var id=Interlocked.Increment(ref serial).ToString("D4");
  var name=$"{id}-{sha[..12]}"; var path=Path.Combine(incoming,name+".body.json");
  using var parsed=JsonDocument.Parse(body); var messages=parsed.RootElement.GetProperty("messages");
  string unit="unknown"; try { var c=messages[1].GetProperty("content"); var text=c.ValueKind==JsonValueKind.String?c.GetString()!:c[0].GetProperty("text").GetString(); var n=JsonNode.Parse(text!); unit=n?["unit"]?.GetValue<string>()??n?["stage"]?.GetValue<string>()??"unknown"; } catch {}
  var meta=JsonSerializer.Serialize(new { id,unit,requestSha256=sha,requestBytes=body.Length,network="not sent; intercepted in-process",route=req.RequestUri.AbsoluteUri });
  await File.WriteAllTextAsync(path,meta+"\n"+Encoding.UTF8.GetString(body),ct);
  await File.WriteAllBytesAsync(Path.Combine(incoming,name+".exact.body.json"),body,ct);
  string exactContent; string finish="stop"; int prompt=Math.Max(1,body.Length/4), completion;
  if(!replayMap.TryGetValue(sha,out exactContent!)) {
   var answerPath=Path.Combine(responses,name+".reply.json");
   while(!File.Exists(answerPath)) await Task.Delay(200,ct);
   using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(answerPath,ct)); var root=doc.RootElement;
   if(!root.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.String) throw new HttpRequestException("LOCAL_LUNA_REPLY_CONTENT_MISSING:"+sha);
   exactContent=content.GetString()!; finish=root.TryGetProperty("finishReason",out var fr)?fr.GetString()??"stop":"stop";
  }
  completion=Math.Max(1,exactContent.Length/4);
  var delta=new Dictionary<string,object?> { ["content"] = exactContent };
  var envelope=BuildSse(delta,finish,body.Length,prompt,completion);
  return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(envelope,Encoding.UTF8,"text/event-stream")};
 }
 static string BuildSse(object delta,string finish,int bytes,int prompt,int completion) {
  static string E(object x)=>"data: "+JsonSerializer.Serialize(x)+"\n\n";
  return ": LOCAL LUNA QUALIFICATION BRIDGE; SYNTHETIC USAGE\n\n"+E(new {id="local-luna",provider="Alibaba",model="qwen/qwen3.7-flash",choices=new[]{new{index=0,delta}}})+E(new{id="local-luna",provider="Alibaba",model="qwen/qwen3.7-flash",choices=new[]{new{index=0,delta=new{},finish_reason=finish}},usage=new{prompt_tokens=prompt,completion_tokens=completion,completion_tokens_details=new{reasoning_tokens=0},cost=0.0}})+"data: [DONE]\n\n";
 }
}


