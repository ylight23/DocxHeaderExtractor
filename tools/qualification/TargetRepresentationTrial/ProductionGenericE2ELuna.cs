using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Agents;
using DocxHeaderExtractor.Agents.Budget;
using DocxHeaderExtractor.Conversation;
using DocxHeaderExtractor.HeadingAgent.Formats;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Runtime;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Contracts.Models;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.DocumentProcessing.Placement;
using DocxHeaderExtractor.Sources.Pdf;

/// <summary>Single-arm qualification through the promoted production PDF selection and grouping builders.</summary>
internal static class ProductionGenericE2ELuna
{
    const int DefaultMembershipVersion = 12;
    const string Mode = "production-generic-e2e-luna";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task ResumeFull(string pdfPath, string output)
    {
        var instruction=PdfSelector.ResolveInstruction(null,true,12);
        var source=AgentSource.Load(pdfPath,out var failure,instruction:instruction,sourceBound:true,rasterGeometry:true)
            ?? throw new InvalidOperationException("SOURCE_LOAD:"+failure);
        var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"selection.json")));
        var outcomes=JsonSerializer.Deserialize<List<AgentUnitOutcome>>(saved.RootElement.GetProperty("outcomes").GetRawText(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ?? throw new InvalidOperationException("FROZEN_SELECTION_READ_FAILED");
        if(outcomes.Count!=source.Cores.Count || outcomes.Any(o=>o.Status!="COMPLETE")) throw new InvalidOperationException("FROZEN_SELECTION_INCOMPLETE");
        using var deadline=new CancellationTokenSource(TimeSpan.FromHours(12)); var budget=AgentSelector.BudgetFor(source,instruction); var trace=new List<object>();
        ModelTurn Call(string phase,int turn,PreparedRequest request)
        {
            if(phase.StartsWith("VERIFY",StringComparison.Ordinal)) request=AddVerifierGeometry(request,source);
            var sha=AgentProtocol.Sha(request.Body); if(sha!=request.Sha256)throw new InvalidOperationException("REQUEST_SHA_MISMATCH");
            if(source.ResourcePolicy.Refusal(RequestResources.Measure(request,source.ResourcePolicy)) is { } refused)throw new InvalidOperationException("ACTUAL_WIRE_REFUSED:"+refused);
            var reqDir=Path.Combine(output,"luna-requests");var resDir=Path.Combine(output,"luna-responses");Directory.CreateDirectory(reqDir);Directory.CreateDirectory(resDir);
            File.WriteAllText(Path.Combine(reqDir,sha+".request.json"),request.Body);var responsePath=Path.Combine(resDir,sha+".response.json");
            Console.WriteLine($"LUNA_REQUEST_READY phase={phase} turn={turn} sha256={sha} response={responsePath}");
            var prior=-1L;var stable=0;while(stable<2){deadline.Token.ThrowIfCancellationRequested();if(!File.Exists(responsePath)){Thread.Sleep(200);continue;}var n=new FileInfo(responsePath).Length;stable=n==prior&&n>0?stable+1:0;prior=n;Thread.Sleep(200);}
            using var doc=JsonDocument.Parse(File.ReadAllText(responsePath));var root=doc.RootElement;if(root.GetProperty("requestSha256").GetString()!=sha)throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
            var calls=root.TryGetProperty("toolCalls",out var cs)?cs.EnumerateArray().Select(c=>new ToolCall(c.GetProperty("id").GetString(),c.GetProperty("name").GetString(),c.GetProperty("arguments").GetString()??"")).ToArray():[];
            var content=root.TryGetProperty("content",out var text)?(text.ValueKind==JsonValueKind.String?text.GetString():text.ToString()):null;
            var reply=new ModelTurn(content,calls,root.TryGetProperty("finishReason",out var fr)?fr.GetString():"stop");trace.Add(new{phase,turn,requestSha256=sha,reply});
            Save(Path.Combine(output,"continuation-model-replies.json"),trace);return reply;
        }
        var chosen=outcomes.SelectMany(o=>o.Decisions).Where(p=>p.Value=="SELECT").Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);
        var extents=outcomes.SelectMany(o=>o.Extents??new Dictionary<string,AgentProtocol.HeadingExtent>()).ToDictionary(k=>k.Key,k=>new TextOffsetSpan(k.Value.Start,k.Value.End),StringComparer.Ordinal);
        var readings=outcomes.SelectMany(o=>o.ReadingChoices??new Dictionary<string,string>()).ToDictionary(k=>k.Key,k=>k.Value,StringComparer.Ordinal);
        var sw=System.Diagnostics.Stopwatch.StartNew();
        var grouped=AgentGrouping.Group(source,chosen,Call,budget,1,deadline.Token,5000,PdfGeneralBoundaryInstruction.Instruction,headingExtents:extents,headingReadings:readings,pairwisePdf:true,generalPdfPrompt:true);
        Save(Path.Combine(output,"grouping.json"),new{seconds=sw.Elapsed.TotalSeconds,result=grouped});
        if(!grouped.Groups.SelectMany(g=>g).ToHashSet(StringComparer.Ordinal).SetEquals(chosen))throw new InvalidOperationException("GROUP_CHANGED_MEMBERSHIP");
        sw.Restart();var verified=GroupVerifier.Verify(source,grouped,extents,readings,Call,budget,1,deadline.Token,5000,instruction,allowMembershipReview:false);
        Save(Path.Combine(output,"verification.json"),new{seconds=sw.Elapsed.TotalSeconds,result=verified,note="Qualification-only semantic verification. The qualification extension adds the same scoped OpenCV page geometry measurements as source observations."});
        var unresolved=verified.UnresolvedAliases.Concat(verified.Provenance.Where(p=>p.Decision=="FALLBACK").Select(p=>p.Alias)).ToHashSet(StringComparer.Ordinal);
        var finalGroups=verified.Groups.Where(g=>g.All(a=>chosen.Contains(a)&&!unresolved.Contains(a))).ToArray();var finalAliases=finalGroups.SelectMany(g=>g).ToHashSet(StringComparer.Ordinal);
        var selected=outcomes.SelectMany(o=>o.Decisions).ToDictionary(k=>k.Key,k=>k.Value,StringComparer.Ordinal);
        var states=selected.ToDictionary(k=>k.Key,k=>k.Value!="SELECT"?k.Value:verified.Memberships.GetValueOrDefault(k.Key)=="NOT_HEADING"?"NOT_SELECT":finalAliases.Contains(k.Key)?"SELECT":"UNRESOLVED",StringComparer.Ordinal);
        Save(Path.Combine(output,"final-headings.json"),new{source.SourceSha256,productionBaselineGroups=grouped.Groups,postVerifyGroups=finalGroups,
            selectedAliases=finalAliases.Order().ToArray(),withheldAliases=chosen.Except(finalAliases).Order().ToArray(),finalStates=states,verified.Memberships,verified.Failures,verified.UnresolvedAliases,
            finalHeadings=finalGroups.Select(g=>new{aliases=g,text=string.Join(" ",g.Select(a=>{var l=source.Lines.Single(x=>x.Alias==a);return extents.TryGetValue(a,out var e)?l.Text[e.Start..e.End]:l.Text;}))})});
        if(finalGroups.Length>0)
        {
            var sourceIds=ReadParserAliases(pdfPath);var atoms=source.Lines.Select(l=>new SemanticSourceAtom(l.Alias,sourceIds[l.Alias],l.Ordinal,l.Page??0,l.Ordinal,
                source.Cores.Select((c,i)=>(c,i)).Single(x=>l.Ordinal>=x.c.Start&&l.Ordinal<x.c.End).i,l.Text)).ToArray();
            var bound=finalGroups.Select(g=>{var parts=g.Select(a=>{var l=source.Lines.Single(x=>x.Alias==a);var span=extents.TryGetValue(a,out var e)?e:new TextOffsetSpan(0,l.Text.Length);
                return new CanonicalSemanticBoundPart(a,sourceIds[a],l.Ordinal,l.Text[span.Start..span.End],span.Start,span.End);}).OrderBy(p=>p.SourceOrdinal).ToArray();var p0=parts[0];
                return new CanonicalSemanticBoundHeading(p0.Alias,p0.SourceId,p0.SourceOrdinal,string.Join(" ",parts.Select(p=>p.Text)),"ESTABLISHES_STRUCTURE","heading","document_body",[],p0.Start,parts[^1].End){Parts=parts};}).OrderBy(h=>h.SourceOrdinal).ToArray();
            var aliasStates=source.Lines.ToDictionary(l=>l.Alias,l=>new HeadingAliasState(states.GetValueOrDefault(l.Alias,"UNRESOLVED"),states.GetValueOrDefault(l.Alias,"UNRESOLVED"),verified.Memberships.GetValueOrDefault(l.Alias),finalAliases.Contains(l.Alias)?"BOUND":"NOT_BOUND"),StringComparer.Ordinal);
            var evidence=new HeadingPlacementEvidence(source.Flavor.PlacementContext(source).DocumentStylesJson,source.Flavor.PlacementContext(source).StyleJsonByAlias,
                source.Flavor.PlacementContext(source).MeasurementsNote){Source=new HeadingPlacementSource(atoms,aliasStates,source.SourceSha256)};
            var placer=new HeadingPlacer(prepared=>Call("HIERARCHY_PLACE",1,prepared),repairParentChains:true);
            var actual=await ParentResolver.PlaceAsync(bound,placer,new ModelAccess("LOCAL_LUNA_IN_SESSION_ONLY",()=>{}),evidence,deadline.Token);
            Save(Path.Combine(output,"placement.json"),new{contract=placer.Contract,headings=actual.Headings,audit=actual.Observation});
        }
        Console.WriteLine($"FULL_E2E_RESUMED selected={chosen.Count} baselineGroups={grouped.Groups.Count} postVerifyGroups={finalGroups.Length}");
    }

    static Dictionary<string,string> ReadParserAliases(string pdfPath)
    {
        var asm=System.Reflection.Assembly.Load("DocxHeaderExtractor.Sources");var adapter=asm.GetType("DocxHeaderExtractor.Sources.Pdf.PdfSourceAdapter")!;
        var method=adapter.GetMethods(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Single(m=>m.Name=="BuildWithDetails"&&m.GetParameters().Length==1);
        var built=method.Invoke(null,[pdfPath])!;var snapshot=built.GetType().GetProperty("Snapshot")!.GetValue(built)!;
        var atoms=(System.Collections.IEnumerable)snapshot.GetType().GetProperty("Atoms")!.GetValue(snapshot)!;var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var atom in atoms){var type=atom!.GetType();result[(string)type.GetProperty("Alias")!.GetValue(atom)!]=(string)type.GetProperty("SourceId")!.GetValue(atom)!;}return result;
    }

    internal static async Task PlaceFrozen(string pdfPath,string output)
    {
        var instruction=PdfSelector.ResolveInstruction(null,true,12);var source=AgentSource.Load(pdfPath,out var failure,instruction:instruction,sourceBound:true,rasterGeometry:true)
            ??throw new InvalidOperationException("SOURCE_LOAD:"+failure);
        var final=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"final-headings.json"))).RootElement;
        var groups=JsonSerializer.Deserialize<string[][]>(final.GetProperty("postVerifyGroups").GetRawText())??[];
        var chosen=JsonSerializer.Deserialize<string[]>(final.GetProperty("selectedAliases").GetRawText())?.ToHashSet(StringComparer.Ordinal)??new(StringComparer.Ordinal);
        var selected=JsonSerializer.Deserialize<Dictionary<string,string>>(final.GetProperty("finalStates").GetRawText())??new(StringComparer.Ordinal);
        var verifiedMembership=JsonSerializer.Deserialize<Dictionary<string,string>>(final.GetProperty("Memberships").GetRawText())??new(StringComparer.Ordinal);
        var frozenSelection=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"selection.json"))).RootElement;
        var outcomes=JsonSerializer.Deserialize<List<AgentUnitOutcome>>(frozenSelection.GetProperty("outcomes").GetRawText(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];
        var extents=outcomes.SelectMany(o=>o.Extents??new Dictionary<string,AgentProtocol.HeadingExtent>()).ToDictionary(k=>k.Key,k=>new TextOffsetSpan(k.Value.Start,k.Value.End),StringComparer.Ordinal);
        var sourceIds=ReadParserAliases(pdfPath);if(sourceIds.Count!=source.Lines.Count)throw new InvalidOperationException("PARSER_ALIAS_COUNT_MISMATCH");
        var atoms=source.Lines.Select(l=>new SemanticSourceAtom(l.Alias,sourceIds[l.Alias],l.Ordinal,l.Page??0,l.Ordinal,
            source.Cores.Select((c,i)=>(c,i)).Single(x=>l.Ordinal>=x.c.Start&&l.Ordinal<x.c.End).i,l.Text)).ToArray();
        var bound=groups.Select(g=>{var parts=g.Select(a=>{var l=source.Lines.Single(x=>x.Alias==a);var span=extents.TryGetValue(a,out var e)?e:new TextOffsetSpan(0,l.Text.Length);
            return new CanonicalSemanticBoundPart(a,sourceIds[a],l.Ordinal,l.Text[span.Start..span.End],span.Start,span.End);}).OrderBy(p=>p.SourceOrdinal).ToArray();var p0=parts[0];
            return new CanonicalSemanticBoundHeading(p0.Alias,p0.SourceId,p0.SourceOrdinal,string.Join(" ",parts.Select(p=>p.Text)),"ESTABLISHES_STRUCTURE","heading","document_body",[],p0.Start,parts[^1].End){Parts=parts};}).OrderBy(h=>h.SourceOrdinal).ToArray();
        var aliasStates=source.Lines.ToDictionary(l=>l.Alias,l=>new HeadingAliasState(selected.GetValueOrDefault(l.Alias,"UNRESOLVED"),selected.GetValueOrDefault(l.Alias,"UNRESOLVED"),verifiedMembership.GetValueOrDefault(l.Alias),chosen.Contains(l.Alias)?"BOUND":"NOT_BOUND"),StringComparer.Ordinal);
        var context=source.Flavor.PlacementContext(source);var evidence=new HeadingPlacementEvidence(context.DocumentStylesJson,context.StyleJsonByAlias,context.MeasurementsNote)
            {Source=new HeadingPlacementSource(atoms,aliasStates,source.SourceSha256)};
        using var deadline=new CancellationTokenSource(TimeSpan.FromHours(12));var trace=new List<object>();
        ModelTurn Call(PreparedRequest request)
        {
            var sha=AgentProtocol.Sha(request.Body);if(sha!=request.Sha256)throw new InvalidOperationException("PLACEMENT_REQUEST_SHA_MISMATCH");
            var reqDir=Path.Combine(output,"luna-requests");var resDir=Path.Combine(output,"luna-responses");Directory.CreateDirectory(reqDir);Directory.CreateDirectory(resDir);
            File.WriteAllText(Path.Combine(reqDir,sha+".request.json"),request.Body);var rp=Path.Combine(resDir,sha+".response.json");
            Console.WriteLine($"LUNA_REQUEST_READY phase=HIERARCHY_PLACE sha256={sha} response={rp}");var prior=-1L;var stable=0;
            while(stable<2){deadline.Token.ThrowIfCancellationRequested();if(!File.Exists(rp)){Thread.Sleep(200);continue;}var n=new FileInfo(rp).Length;stable=n==prior&&n>0?stable+1:0;prior=n;Thread.Sleep(200);}
            using var doc=JsonDocument.Parse(File.ReadAllText(rp));var root=doc.RootElement;if(root.GetProperty("requestSha256").GetString()!=sha)throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
            var calls=root.TryGetProperty("toolCalls",out var cs)?cs.EnumerateArray().Select(c=>new ToolCall(c.GetProperty("id").GetString(),c.GetProperty("name").GetString(),c.GetProperty("arguments").GetString()??"")).ToArray():[];
            var content=root.TryGetProperty("content",out var v)?(v.ValueKind==JsonValueKind.String?v.GetString():v.ToString()):null;var reply=new ModelTurn(content,calls,root.TryGetProperty("finishReason",out var fr)?fr.GetString():"stop");
            trace.Add(new{requestSha256=sha,reply});Save(Path.Combine(output,"placement-model-replies.json"),trace);return reply;
        }
        var placer=new HeadingPlacer(prepared=>Call(prepared),repairParentChains:true);
        var result=await ParentResolver.PlaceAsync(bound,placer,new ModelAccess("LOCAL_LUNA_IN_SESSION_ONLY",()=>{}),evidence,deadline.Token);
        Save(Path.Combine(output,"placement.json"),new{contract=placer.Contract,stage="postVerify groups, which equal production GROUP baseline on this run",headings=result.Headings,audit=result.Observation});
        var derived=DocxHeaderExtractor.DocumentProcessing.Hierarchy.HierarchyResolver.DeriveHierarchyFromModelRelations(result.Headings).ToDictionary(h=>h.SourceId,StringComparer.Ordinal);
        Save(Path.Combine(output,"hierarchy-final-headings.json"),new{source.SourceSha256,groups=result.Headings.Select(h=>new{h.Alias,h.SourceId,h.SourceOrdinal,h.Text,h.Parts,parent=h.RelationHints.FirstOrDefault(r=>r.StartsWith("parent-node:",StringComparison.Ordinal))?[12..],level=derived[h.SourceId].Level,resolution=derived[h.SourceId].Resolution}),audit=result.Observation});
        Console.WriteLine($"PLACEMENT_COMPLETE headings={bound.Length} calls={result.Observation.LogicalCalls} unresolved={result.Observation.UnresolvedCount} repairCalls={result.Observation.RepairCount}");
    }

    internal static async Task RunFull(string pdfPath, string output)
    {
        var instruction = PdfSelector.ResolveInstruction(null, true, 12);
        if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var source = AgentSource.Load(pdfPath, out var failure, instruction: instruction, sourceBound: true, rasterGeometry: true)
            ?? throw new InvalidOperationException("SOURCE_LOAD:" + failure);
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(12));
        var budget = AgentSelector.BudgetFor(source, instruction);
        var trace = new List<object>();
        ModelTurn Call(string phase, int turn, PreparedRequest request)
        {
            var sha = AgentProtocol.Sha(request.Body);
            if (sha != request.Sha256) throw new InvalidOperationException("REQUEST_SHA_MISMATCH");
            if (source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy)) is { } refusal)
                throw new InvalidOperationException("ACTUAL_WIRE_REFUSED:" + refusal);
            var reqDir = Path.Combine(output, "luna-requests"); var resDir = Path.Combine(output, "luna-responses");
            Directory.CreateDirectory(reqDir); Directory.CreateDirectory(resDir);
            File.WriteAllText(Path.Combine(reqDir, sha + ".request.json"), request.Body);
            var responsePath = Path.Combine(resDir, sha + ".response.json");
            Console.WriteLine($"LUNA_REQUEST_READY phase={phase} turn={turn} sha256={sha} response={responsePath}");
            while (!File.Exists(responsePath)) { deadline.Token.ThrowIfCancellationRequested(); Thread.Sleep(200); }
            var last = -1L; var stable = 0;
            while (stable < 2) { deadline.Token.ThrowIfCancellationRequested(); var n = new FileInfo(responsePath).Length; stable = n == last && n > 0 ? stable + 1 : 0; last = n; Thread.Sleep(200); }
            using var doc = JsonDocument.Parse(File.ReadAllText(responsePath)); var root = doc.RootElement;
            if (root.GetProperty("requestSha256").GetString() != sha) throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
            var calls = root.TryGetProperty("toolCalls", out var cs) ? cs.EnumerateArray().Select(c => new ToolCall(c.GetProperty("id").GetString(), c.GetProperty("name").GetString(), c.GetProperty("arguments").GetString() ?? "")).ToArray() : [];
            var reply = new ModelTurn(root.TryGetProperty("content", out var content) ? (content.ValueKind == JsonValueKind.String ? content.GetString() : content.ToString()) : null, calls,
                root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop");
            trace.Add(new { phase, turn, requestSha256 = sha, reply }); Save(Path.Combine(output, "model-replies.json"), trace);
            return reply;
        }
        Save(Path.Combine(output, "run-identity.json"), new { mode = "production-full-e2e-luna", pdfPath, source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            source.Cores, scopes = source.Plan, aliases = source.Lines.Count, membershipInstructionVersion = 12, sourceBoundSelection = true,
            rasterGeometry = true, genericPairwiseGrouping = true, verifier = "qualification-only", placement = "production HeadingPlacer default 16-alias batches and native parent-chain repair", goldInRequests = false });
        var outcomes = new List<AgentUnitOutcome>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var segment = 0; segment < source.Cores.Count; segment++)
        {
            var task = new SelectionTask(source, segment, instruction, null, null);
            var result = ToolConversation.Run(task, Call, budget, 5000, maxPosts: 6);
            outcomes.Add(result); Save(Path.Combine(output, "selection-progress.json"), new { completed = outcomes.Count, total = source.Cores.Count, outcomes });
            Console.WriteLine($"SCOPE_COMPLETE segment={segment} status={result.Status} elapsed={clock.Elapsed.TotalSeconds:0}");
        }
        Save(Path.Combine(output, "selection.json"), new { seconds = clock.Elapsed.TotalSeconds, outcomes });
        var scopeAliasSet = source.Cores.SelectMany((core, i) => source.Lines.Skip(core.Start).Take(core.End-core.Start).Select(l => l.Alias)).ToHashSet(StringComparer.Ordinal);
        var decided = outcomes.SelectMany(o => o.Decisions).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var owners = source.Cores.SelectMany((core, i) => source.Lines.Skip(core.Start).Take(core.End-core.Start).Select(l => (l.Alias, Owner: i))).ToDictionary(x => x.Alias, x => x.Owner, StringComparer.Ordinal);
        if (!scopeAliasSet.SetEquals(owners.Keys) || decided.Keys.Any(a => !owners.ContainsKey(a))) throw new InvalidOperationException("SELECTION_ALIAS_OWNERSHIP_MISMATCH");
        if (outcomes.Any(o => o.Status != "COMPLETE")) throw new InvalidOperationException("SELECTION_INCOMPLETE_SCOPES");
        var chosen = decided.Where(p => p.Value == "SELECT").Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var extents = outcomes.SelectMany(o => o.Extents ?? new Dictionary<string, AgentProtocol.HeadingExtent>()).ToDictionary(kv => kv.Key, kv => new TextOffsetSpan(kv.Value.Start, kv.Value.End), StringComparer.Ordinal);
        var readings = outcomes.SelectMany(o => o.ReadingChoices ?? new Dictionary<string,string>()).ToDictionary(kv=>kv.Key,kv=>kv.Value,StringComparer.Ordinal);
        Save(Path.Combine(output, "production-baseline.json"), new { selectedAliases = chosen.Order().ToArray(), aliasOwners = owners, decisions = decided, outcomes });
        clock.Restart();
        var grouped = AgentGrouping.Group(source, chosen, Call, budget, 1, deadline.Token, 5000, PdfGeneralBoundaryInstruction.Instruction,
            headingExtents: extents, headingReadings: readings, pairwisePdf: true, generalPdfPrompt: true);
        Save(Path.Combine(output, "grouping.json"), new { seconds=clock.Elapsed.TotalSeconds, result=grouped });
        if (!grouped.Groups.SelectMany(g=>g).ToHashSet(StringComparer.Ordinal).SetEquals(chosen)) throw new InvalidOperationException("GROUP_CHANGED_MEMBERSHIP");
        clock.Restart();
        var verified = GroupVerifier.Verify(source, grouped, extents, readings, Call, budget, 1, deadline.Token, 5000, instruction, allowMembershipReview:false);
        Save(Path.Combine(output, "verification.json"), new { seconds=clock.Elapsed.TotalSeconds, result=verified,
            note="Qualification-only semantic verification; verifier packets include the same scoped OpenCV measurements already used by selection/grouping." });
        var unresolved=verified.UnresolvedAliases.Concat(verified.Provenance.Where(p=>p.Decision=="FALLBACK").Select(p=>p.Alias)).ToHashSet(StringComparer.Ordinal);
        var finalGroups=verified.Groups.Where(g=>g.All(a=>chosen.Contains(a)&&!unresolved.Contains(a))).ToArray();
        var finalAliases=finalGroups.SelectMany(g=>g).ToHashSet(StringComparer.Ordinal);
        Save(Path.Combine(output,"final-headings.json"),new { source.SourceSha256, productionBaselineGroups=grouped.Groups,
            postVerifyGroups=finalGroups, selectedAliases=finalAliases.Order().ToArray(), withheldAliases=chosen.Except(finalAliases).Order().ToArray(),
            verificationMembership=verified.Memberships, failures=verified.Failures, unresolved=verified.UnresolvedAliases,
            finalHeadings=finalGroups.Select(g=>new { aliases=g, text=string.Join(" ",g.Select(a=>{var l=source.Lines.Single(x=>x.Alias==a);return extents.TryGetValue(a,out var e)?l.Text[e.Start..e.End]:l.Text;}))}) });
        // Production placement and native bounded parent-chain repair over the settled production GROUP headings.
        var placementHeadings = grouped.Groups.Select(g => { var first=g.OrderBy(a=>source.Lines.Single(l=>l.Alias==a).Ordinal).First(); var line=source.Lines.Single(l=>l.Alias==first);
            var parts=g.Select(a=>{var l=source.Lines.Single(x=>x.Alias==a); var e=extents.TryGetValue(a,out var s)?s:new TextOffsetSpan(0,l.Text.Length); return new CanonicalSemanticBoundPart(a,a,l.Ordinal,l.Text[e.Start..e.End],e.Start,e.End);}).ToArray();
            return new CanonicalSemanticBoundHeading(first,first,line.Ordinal,string.Join(" ",parts.Select(p=>p.Text)),"ESTABLISHES_STRUCTURE","heading","document_body",[],parts[0].Start,parts[^1].End){Parts=parts}; }).OrderBy(h=>h.SourceOrdinal).ToArray();
        var sourceLines=source.Lines.Select(l=>new SemanticSourceAtom(l.Alias,l.Alias,l.Ordinal,l.Page??0,l.Ordinal,owners.GetValueOrDefault(l.Alias),l.Text)).ToArray();
        var aliasStates=source.Lines.ToDictionary(l=>l.Alias,l=>new HeadingAliasState(decided.GetValueOrDefault(l.Alias,"UNRESOLVED"),decided.GetValueOrDefault(l.Alias,"UNRESOLVED"), verified.Memberships.GetValueOrDefault(l.Alias), finalAliases.Contains(l.Alias)?"BOUND":"NOT_BOUND"),StringComparer.Ordinal);
        var evidence=new HeadingPlacementEvidence("{}",new Dictionary<string,string>(),"PDF source text; OpenCV geometry is measured evidence, with no semantic labels."){Source=new HeadingPlacementSource(sourceLines,aliasStates,source.SourceSha256)};
        var placer=new HeadingPlacer(prepared=>Call("HIERARCHY_PLACE",1,prepared),wholeDocument:false,repairParentChains:true);
        var proposal=await placer.ProposeAsync(new PlacementRequest(placementHeadings,evidence),new ModelAccess("LOCAL_LUNA_IN_SESSION_ONLY",()=>{}),deadline.Token);
        var parentMap=proposal.Parents.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        Save(Path.Combine(output,"placement.json"),new { contract=placer.Contract, headings=placementHeadings, proposal, parentMap,
            graphRule="Only ROOT/NONE/UNRESOLVED or earlier bound aliases are valid; full placement graph audit requires DocumentProcessing ParentResolver." });
        Console.WriteLine($"FULL_E2E_COMPLETE aliases={source.Lines.Count} scopes={outcomes.Count} baselineGroups={grouped.Groups.Count} verifiedGroups={finalGroups.Length} placed={proposal.Parents.Count} repairCalls={proposal.ChainRepair?.LogicalCalls ?? 0}");
    }

    internal static Task Run(string pdfPath, string output)
    {
        var Instruction = PdfSelector.ResolveInstruction(null, true, DefaultMembershipVersion);
        if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var source = AgentSource.Load(pdfPath, out var failure, instruction: Instruction, sourceBound: true,
            rasterGeometry: true) ?? throw new InvalidOperationException("SOURCE_LOAD:" + failure);
        var segment = Enumerable.Range(0, source.Cores.Count)
            .Single(i => source.Lines[source.Cores[i].Start].Page == 14);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var budget = AgentSelector.BudgetFor(source, Instruction);
        var trace = new List<object>();
        var task = new SelectionTask(source, segment, Instruction, null, null);
        ModelTurn Call(string phase, int turn, PreparedRequest request)
        {
            if (phase.StartsWith("VERIFY", StringComparison.Ordinal)) request = AddVerifierGeometry(request, source);
            var calculated = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body)));
            if (!string.Equals(calculated, request.Sha256, StringComparison.Ordinal)) throw new InvalidOperationException("REQUEST_SHA_MISMATCH");
            if (source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy)) is { } refusal)
                throw new InvalidOperationException("ACTUAL_WIRE_REFUSED:" + refusal);
            var pending = Path.Combine(output, "luna-requests");
            var authored = Path.Combine(output, "luna-responses");
            Directory.CreateDirectory(pending); Directory.CreateDirectory(authored);
            File.WriteAllText(Path.Combine(pending, request.Sha256 + ".request.json"), request.Body);
            var responsePath = Path.Combine(authored, request.Sha256 + ".response.json");
            Console.WriteLine($"LUNA_REQUEST_READY mode={Mode} phase={phase} turn={turn} sha256={request.Sha256} response={responsePath}");
            while (!File.Exists(responsePath)) { deadline.Token.ThrowIfCancellationRequested(); Thread.Sleep(200); }
            using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
            var root = doc.RootElement;
            if (root.GetProperty("requestSha256").GetString() != request.Sha256) throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
            var calls = root.TryGetProperty("toolCalls", out var cs)
                ? cs.EnumerateArray().Select(c => new ToolCall(c.GetProperty("id").GetString(), c.GetProperty("name").GetString(), c.GetProperty("arguments").GetString() ?? "")).ToArray()
                : Array.Empty<ToolCall>();
            var reply = new ModelTurn(root.TryGetProperty("content", out var content) ? content.GetString() : null, calls,
                root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop");
            trace.Add(new { phase, turn, requestSha256 = request.Sha256, reply });
            Save(Path.Combine(output, "model-replies.json"), trace);
            return reply;
        }

        Save(Path.Combine(output, "run-identity.json"), new
        {
            mode = Mode, pdfPath, source.SourceSha256, source.SourceAliasUniverseSha256, source.EvidenceStoreSha256,
            page = 14, segment, core = source.Cores[segment], membershipInstructionVersion = DefaultMembershipVersion,
            sourceBoundSelection = true, rasterGeometry = true, separateGroupingInstruction = true,
            pairwiseGrouping = true, generalBoundaryPrompt = true,
            selectionRequestBuilder = "production SelectionTask", groupingRequestBuilder = "production AgentGrouping.Group",
            verifier = "qualification-only GroupVerifier; geometry evidence is explicitly added as a qualification extension",
            goldInRequests = false
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var selected = ToolConversation.Run(task, Call, budget, 5000, maxPosts: 6);
        Save(Path.Combine(output, "selection.json"), new { seconds = sw.Elapsed.TotalSeconds, result = selected });
        if (selected.Status != "COMPLETE") throw new InvalidOperationException("SELECTION_" + selected.Status);
        var chosen = selected.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var extents = selected.Extents?.ToDictionary(e => e.Key, e => new TextOffsetSpan(e.Value.Start, e.Value.End)) ?? [];
        var readings = selected.ReadingChoices ?? new Dictionary<string, string>();

        sw.Restart();
        var grouped = AgentGrouping.Group(source, chosen, Call, budget, 1, deadline.Token, 5000,
            PdfGeneralBoundaryInstruction.Instruction, headingExtents: extents, headingReadings: readings,
            pairwisePdf: true, generalPdfPrompt: true);
        Save(Path.Combine(output, "grouping.json"), new { seconds = sw.Elapsed.TotalSeconds, result = grouped });
        if (!grouped.Groups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal).SetEquals(chosen))
            throw new InvalidOperationException("GROUP_CHANGED_MEMBERSHIP");

        sw.Restart();
        var verified = GroupVerifier.Verify(source, grouped, extents, readings, Call, budget, 1, deadline.Token,
            5000, Instruction, allowMembershipReview: false);
        Save(Path.Combine(output, "verification.json"), new { seconds = sw.Elapsed.TotalSeconds, result = verified,
            note = "Qualification-only semantic verification; VERIFY requests add the same scoped pageGeometryEvidence shape because production verifier currently does not attach it." });
        var unresolved = verified.UnresolvedAliases.Concat(verified.Provenance.Where(p => p.Decision == "FALLBACK").Select(p => p.Alias)).ToHashSet(StringComparer.Ordinal);
        var finals = verified.Groups.Where(g => g.All(a => chosen.Contains(a) && !unresolved.Contains(a))).ToArray();
        var finalAliases = finals.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var states = selected.Decisions.ToDictionary(d => d.Key, d => d.Value != "SELECT" ? d.Value
            : verified.Memberships.GetValueOrDefault(d.Key) == "NOT_HEADING" ? "NOT_SELECT"
            : finalAliases.Contains(d.Key) ? "SELECT" : "UNRESOLVED", StringComparer.Ordinal);
        Save(Path.Combine(output, "final-headings.json"), new
        {
            source.SourceSha256, page = 14, mode = Mode,
            pipeline = "SELECT -> native production pairwise GROUP (generic prompt) -> qualification semantic VERIFY -> source-bound final groups",
            finalStates = states, finalGroups = finals, selectedAliases = finalAliases.Order().ToArray(),
            withheldAliases = chosen.Except(finalAliases).Order().ToArray(), verified.Memberships, verified.Failures, verified.UnresolvedAliases,
            finalHeadings = finals.Select(g => new { aliases = g, text = string.Join(" ", g.Select(a =>
            { var line = source.Lines.Single(l => l.Alias == a); return extents.TryGetValue(a, out var span) ? line.Text[span.Start..span.End] : line.Text; })) }),
            unresolvedOrNotSelected = states.Where(kv => kv.Value is "UNRESOLVED" or "NOT_SELECT").ToDictionary(kv => kv.Key, kv => kv.Value)
        });
        Console.WriteLine($"PRODUCTION_GENERIC_E2E_COMPLETE selected={chosen.Count} final={finalAliases.Count} unresolved={states.Count(kv => kv.Value == "UNRESOLVED")}");
        return Task.CompletedTask;
    }

    internal static void Resume(string pdfPath, string output, string frozenSelectionDirectory)
    {
        // A continuation keeps its original instruction even after application defaults change.
        using var originalIdentity = JsonDocument.Parse(File.ReadAllText(Path.Combine(frozenSelectionDirectory, "run-identity.json")));
        var Instruction = PdfSelector.ResolveInstruction(null, true,
            originalIdentity.RootElement.GetProperty("membershipInstructionVersion").GetInt32());
        if (Directory.Exists(output)) throw new InvalidOperationException("REFUSE_OVERWRITING_TRIAL");
        Directory.CreateDirectory(output);
        var frozen = JsonDocument.Parse(File.ReadAllText(Path.Combine(frozenSelectionDirectory, "selection.json")));
        var selection = JsonSerializer.Deserialize<AgentUnitOutcome>(frozen.RootElement.GetProperty("result").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("FROZEN_SELECTION_READ_FAILED");
        if (selection.Status != "COMPLETE" || selection.UnitId != "SEG-013") throw new InvalidOperationException("FROZEN_SELECTION_NOT_COMPLETE_PAGE14");
        var source = AgentSource.Load(pdfPath, out var failure, instruction: Instruction, sourceBound: true,
            rasterGeometry: true) ?? throw new InvalidOperationException("SOURCE_LOAD:" + failure);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var budget = AgentSelector.BudgetFor(source, Instruction);
        var trace = new List<object>();
        ModelTurn Call(string phase, int turn, PreparedRequest request)
        {
            if (phase.StartsWith("VERIFY", StringComparison.Ordinal)) request = AddVerifierGeometry(request, source);
            var sha = AgentProtocol.Sha(request.Body);
            if (sha != request.Sha256) throw new InvalidOperationException("REQUEST_SHA_MISMATCH");
            if (source.ResourcePolicy.Refusal(RequestResources.Measure(request, source.ResourcePolicy)) is { } refusal)
                throw new InvalidOperationException("ACTUAL_WIRE_REFUSED:" + refusal);
            var requestDir = Path.Combine(output, "luna-requests");
            var responseDir = Path.Combine(output, "luna-responses");
            Directory.CreateDirectory(requestDir); Directory.CreateDirectory(responseDir);
            File.WriteAllText(Path.Combine(requestDir, sha + ".request.json"), request.Body);
            var responsePath = Path.Combine(responseDir, sha + ".response.json");
            Console.WriteLine($"LUNA_REQUEST_READY mode=production-generic-e2e-luna-resume phase={phase} turn={turn} sha256={sha} response={responsePath}");
            var lastLength = -1L; var stable = 0;
            while (stable < 2)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!File.Exists(responsePath)) { Thread.Sleep(200); continue; }
                var length = new FileInfo(responsePath).Length;
                stable = length == lastLength && length > 0 ? stable + 1 : 0;
                lastLength = length;
                Thread.Sleep(200);
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(responsePath));
                var root = doc.RootElement;
                if (root.GetProperty("requestSha256").GetString() != sha) throw new InvalidOperationException("LUNA_REPLY_REQUEST_SHA_MISMATCH");
                var calls = root.TryGetProperty("toolCalls", out var cs)
                    ? cs.EnumerateArray().Select(c => new ToolCall(c.GetProperty("id").GetString(), c.GetProperty("name").GetString(), c.GetProperty("arguments").GetString() ?? "")).ToArray()
                    : Array.Empty<ToolCall>();
                var reply = new ModelTurn(root.TryGetProperty("content", out var content) ? content.GetString() : null, calls,
                    root.TryGetProperty("finishReason", out var fr) ? fr.GetString() : "stop");
                trace.Add(new { phase, turn, requestSha256 = sha, reply });
                Save(Path.Combine(output, "model-replies.json"), trace);
                return reply;
            }
            catch (Exception e)
            {
                File.AppendAllText(Path.Combine(output, "bridge-errors.txt"), $"{phase}/{turn}/{sha}: {e.GetType().FullName}: {e.Message}{Environment.NewLine}");
                throw;
            }
        }

        var chosen = selection.Decisions.Where(d => d.Value == "SELECT").Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var extents = selection.Extents?.ToDictionary(e => e.Key, e => new TextOffsetSpan(e.Value.Start, e.Value.End)) ?? [];
        var readings = selection.ReadingChoices ?? new Dictionary<string, string>();
        Save(Path.Combine(output, "continuation-identity.json"), new
        {
            mode = "production-generic-e2e-luna-resume", frozenSelectionDirectory, selectionRequestSha256 = selection.RequestSha256s,
            source.SourceSha256, page = 14, selectedAliasCount = chosen.Count,
            note = "SELECT is loaded from the frozen initial run and is not repeated."
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grouped = AgentGrouping.Group(source, chosen, Call, budget, 1, deadline.Token, 5000,
            PdfGeneralBoundaryInstruction.Instruction, headingExtents: extents, headingReadings: readings,
            pairwisePdf: true, generalPdfPrompt: true);
        Save(Path.Combine(output, "grouping.json"), new { seconds = sw.Elapsed.TotalSeconds, result = grouped });
        if (!grouped.Groups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal).SetEquals(chosen))
            throw new InvalidOperationException("GROUP_CHANGED_MEMBERSHIP");
        sw.Restart();
        var verified = GroupVerifier.Verify(source, grouped, extents, readings, Call, budget, 1, deadline.Token,
            5000, Instruction, allowMembershipReview: false);
        Save(Path.Combine(output, "verification.json"), new { seconds = sw.Elapsed.TotalSeconds, result = verified,
            note = "Qualification-only semantic verification; VERIFY requests add scoped pageGeometryEvidence as a qualification extension." });
        var unresolved = verified.UnresolvedAliases.Concat(verified.Provenance.Where(p => p.Decision == "FALLBACK").Select(p => p.Alias)).ToHashSet(StringComparer.Ordinal);
        var finals = verified.Groups.Where(g => g.All(a => chosen.Contains(a) && !unresolved.Contains(a))).ToArray();
        var finalAliases = finals.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        Save(Path.Combine(output, "final-headings.json"), new
        {
            source.SourceSha256, page = 14, mode = "production-generic-e2e-luna-resume",
            finalGroups = finals, selectedAliases = finalAliases.Order().ToArray(), withheldAliases = chosen.Except(finalAliases).Order().ToArray(),
            verified.Memberships, verified.Failures, verified.UnresolvedAliases,
            finalHeadings = finals.Select(g => new { aliases = g, text = string.Join(" ", g.Select(a =>
            { var line = source.Lines.Single(l => l.Alias == a); return extents.TryGetValue(a, out var span) ? line.Text[span.Start..span.End] : line.Text; })) })
        });
        Console.WriteLine($"PRODUCTION_GENERIC_E2E_RESUME_COMPLETE selected={chosen.Count} final={finalAliases.Count} unresolved={chosen.Except(finalAliases).Count()}");
    }

    static PreparedRequest AddVerifierGeometry(PreparedRequest request, AgentSource source)
    {
        var wire = JsonNode.Parse(request.Body)!.AsObject();
        var message = wire["messages"]!.AsArray()[1]!;
        var content = message["content"]!;
        var payloadText = content is JsonArray ? content[0]!["text"]!.GetValue<string>() : content.GetValue<string>();
        var payload = JsonNode.Parse(payloadText)!.AsObject();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var known = source.Lines.Select(l => l.Alias).ToHashSet(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var item in obj) Visit(item.Value);
            else if (node is JsonArray array) foreach (var item in array) Visit(item);
            else if (node is JsonValue value && value.TryGetValue<string>(out var text) && known.Contains(text)) aliases.Add(text);
        }
        Visit(payload);
        var pages = new JsonArray();
        foreach (var page in aliases.Select(a => source.Lines.Single(l => l.Alias == a).Page ?? 0).Distinct().Order())
        {
            if (!source.RasterGeometry.TryGetValue(page, out var measurements)) continue;
            var scoped = measurements.DeepClone().AsObject();
            if (scoped["occurrences"] is JsonArray occurrences)
            {
                var items = occurrences.Where(o => aliases.Contains(o!["alias"]!.GetValue<string>())).Select(o => o!.DeepClone()).ToArray();
                foreach (var item in items)
                    foreach (var field in new[] { "verticalOverlapPeers", "nearestLeftAlignedPeers" })
                        if (item[field] is JsonArray peers)
                            item[field] = new JsonArray(peers.Where(p => aliases.Contains(p!.GetValue<string>())).Select(p => p!.DeepClone()).ToArray());
                scoped["pageOccurrenceCount"] = occurrences.Count;
                scoped["scopeOccurrenceCount"] = items.Length;
                scoped["occurrences"] = new JsonArray(items);
            }
            pages.Add(scoped);
        }
        payload["pageGeometryEvidence"] = new JsonObject { ["version"] = PdfGeometryMeasurements.Version, ["semanticLabels"] = "NONE",
            ["scope"] = "aliases already shown in this packet; page-wide measurements remain marked as such", ["pages"] = pages };
        var updatedPayload = payload.ToJsonString(AgentUnit.ReviewJson);
        if (content is JsonArray) content[0]!["text"] = updatedPayload;
        else message["content"] = updatedPayload;
        var systemMessage = wire["messages"]!.AsArray()[0]!;
        var system = systemMessage["content"]!;
        const string note = "\nQualification extension: pageGeometryEvidence contains scoped OpenCV measurements already used in production selection/group requests. These are measurements, not semantic labels; use with source text and context.";
        if (system is JsonArray) system[0]!["text"] = system[0]!["text"]!.GetValue<string>() + note;
        else systemMessage["content"] = system.GetValue<string>() + note;
        var body = wire.ToJsonString(AgentUnit.ReviewJson);
        return new(body, Encoding.UTF8.GetByteCount(body), AgentProtocol.Sha(body));
    }

    static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}
