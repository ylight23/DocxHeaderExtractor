using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.Contracts.Provider;
using DocxHeaderExtractor.DocumentProcessing.Harness;
using DocxHeaderExtractor.HeadingAgent.Placement;
using DocxHeaderExtractor.HeadingAgent.Selectors;
using DocxHeaderExtractor.Agents.Transport;

namespace DocxHeaderExtractor.App;

/// <summary>Opt-in private evidence for a web qualification run. Never records HTTP headers.</summary>
public sealed class DiagnosticCaptureSettings
{
    readonly string? root;

    /// <summary>
    /// Optional lower cost ceiling for a captured qualification run (<c>DHX_CAPTURE_MAX_USD</c>), only when capture is on. It caps both the
    /// per-extraction provider budget and the advice model's ceiling. It is a reservation ceiling for parallel requests, not the expected spend:
    /// a value far below 0.5 USD can refuse requests and fail the run.
    /// </summary>
    public double? MaxRunUsd { get; }

    public DiagnosticCaptureSettings(string? configured, string workingDirectory, string? maxRunUsd = null)
    {
        if (string.IsNullOrWhiteSpace(configured)) return;
        if (!string.IsNullOrWhiteSpace(maxRunUsd))
        {
            if (!double.TryParse(maxRunUsd, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cap) ||
                !double.IsFinite(cap) || cap <= 0)
                throw new InvalidOperationException("DHX_CAPTURE_MAX_USD must be a positive number such as 0.5.");
            MaxRunUsd = cap;
        }
        root = Path.GetFullPath(configured);
        var ancestor = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (ancestor is not null && !Directory.Exists(Path.Combine(ancestor.FullName, ".git"))) ancestor = ancestor.Parent;
        if (ancestor is not null)
        {
            static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var repo = ancestor.FullName;
            if (Within(root, repo) && !Within(root, Path.Combine(repo, "scratchpad")) && !Within(root, Path.Combine(repo, ".dhx-evidence")) &&
                !Within(root, Path.Combine(workingDirectory, ".dhx-evidence")))
                throw new InvalidOperationException("DHX_CAPTURE_DIR must be outside the repository or in its ignored scratchpad/.dhx-evidence directory.");
        }
    }
    public CaptureRun? Start(string? apiKey) => root is null ? null : new CaptureRun(root, apiKey);
}

public sealed record CaptureSummary(int MissingResponses, int MissingPlacementResults, int PlacementCalls, int PlacementWireCalls);

public sealed class CaptureRun
{
    readonly string dir;
    readonly string? key;
    readonly object gate = new();
    int sequence;
    int verifiedFiles;
    readonly ConcurrentDictionary<string, string> writtenHashes = new(StringComparer.Ordinal);
    public CaptureRun(string root, string? key)
    {
        this.key = key;
        dir = Path.Combine(root, "web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }
    public void Record(string name, object payload) => Write(name, JsonSerializer.SerializeToUtf8Bytes(payload));
    void Write(string name, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(key) && Encoding.UTF8.GetString(bytes).Contains(key, StringComparison.Ordinal))
            throw new InvalidDataException("CAPTURE_CONTAINS_CREDENTIAL");
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(path))))
            throw new InvalidDataException("CAPTURE_HASH_MISMATCH");
        writtenHashes[path] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Interlocked.Increment(ref verifiedFiles);
    }
    public HttpMessageHandler Handler(string document, string fallbackStage, HttpMessageHandler? inner = null) => new CaptureHandler(this, document, fallbackStage, inner);
    public IModelProvider Provider(IModelProvider original, PipelineOptions options, string document)
    {
        var selector = new DocxSelector(handlerFactory: () => Handler(document, "SELECT_GROUP"),
            onModelReply: reply => Record($"{document}.selector-reply-{Interlocked.Increment(ref sequence):0000}.json", reply),
            extraction: options.Extraction,
            boundarySnapParagraphs: options.DocxBoundarySnapParagraphs,
            selectionCacheRoot: options.DocxReuseSelection ? options.DocxSelectionCacheRoot : null, splitNumberedGroups: options.DocxSplitNumberedGroups);
        var supplied = original.Create<IHeadingPlacer>(AgentRoles.Placer);
        var placer = supplied ?? new HeadingPlacer(Handler(document, "PLACE"), wholeDocument: options.DocxWholeDocumentPlacement || options.WholeDocumentPlacement,
            repairParentChains: options.RepairParentChains);
        return new CaptureProvider(original, selector, new CapturePlacer(this, document, placer, supplied is null));
    }
    public CaptureSummary Summary()
    {
        var files = Directory.GetFiles(dir);
        var verifiedAtEnd = writtenHashes.Count(pair => File.Exists(pair.Key) &&
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value);
        var leaked = !string.IsNullOrEmpty(key) && files.Any(f => Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains(key, StringComparison.Ordinal));
        var calls = new List<(string Stage, bool Complete)>();
        foreach (var file in files.Where(f => f.EndsWith(".index.json", StringComparison.Ordinal)))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            calls.Add((doc.RootElement.GetProperty("stage").GetString() ?? "UNKNOWN", doc.RootElement.GetProperty("responseAvailable").GetBoolean()));
        }
        var stages = string.Join(", ", calls.GroupBy(c => c.Stage).Select(g => $"{g.Key}={g.Count()}(missing={g.Count(c => !c.Complete)})"));
        var placeResults = files.Where(f => f.EndsWith(".place-result.json", StringComparison.Ordinal)).ToArray();
        var placeCalls = 0;
        foreach (var file in placeResults)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            placeCalls += doc.RootElement.GetProperty("Calls").GetArrayLength();
        }
        var missingPlace = files.Count(f => f.EndsWith(".place-missing.json", StringComparison.Ordinal));
        foreach (var file in placeResults)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (doc.RootElement.GetProperty("missingEvidence").GetBoolean()) missingPlace++;
        }
        Console.WriteLine($"Diagnostic capture: {stages}; PLACE calls={placeCalls}, missing result={missingPlace}; files={files.Length}, verified at end={verifiedAtEnd}/{verifiedFiles}; credentialFound={leaked}; path={dir}");
        if (verifiedAtEnd != verifiedFiles) throw new InvalidDataException("CAPTURE_HASH_MISMATCH");
        if (leaked) throw new InvalidDataException("CAPTURE_CONTAINS_CREDENTIAL");
        return new(calls.Count(c => !c.Complete), missingPlace, placeCalls,
            calls.Count(c => c.Stage.Contains("PLACE", StringComparison.OrdinalIgnoreCase) ||
                c.Stage.Contains("CHAIN_REPAIR", StringComparison.OrdinalIgnoreCase)));
    }
    sealed class CaptureProvider(IModelProvider inner, IHeadingSelector selector, IHeadingPlacer placer) : IModelProvider
    {
        public ModelRoute CreateRoute() => inner.CreateRoute();
        public T? Create<T>(string role) where T : class => role == AgentRoles.DocxSelector ? selector as T :
            role == AgentRoles.Placer ? placer as T : inner.Create<T>(role);
    }
    sealed class CapturePlacer(CaptureRun capture, string document, IHeadingPlacer inner, bool wireCaptured) : IHeadingPlacer
    {
        public string Contract => inner.Contract;
        public async Task<PlacementProposal> ProposeAsync(PlacementRequest request, ModelAccess access, CancellationToken ct)
        {
            capture.Record(document + ".place-request.json", request);
            PlacementProposal proposal;
            try { proposal = await inner.ProposeAsync(request, access, ct); }
            catch { capture.Record(document + ".place-missing.json", new { responseAvailable = false }); throw; }
            capture.Record(document + ".place-result.json", new { proposal.Calls, proposal.Failures, proposal.ModelEvidence, proposal.ChainRepair,
                missingEvidence = proposal.Calls.Count > proposal.ModelEvidence.Count,
                wireCaptureExpected = wireCaptured });
            return proposal;
        }
    }
    sealed class CaptureHandler(CaptureRun capture, string document, string fallbackStage, HttpMessageHandler? inner)
        : DelegatingHandler(inner ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var number = Interlocked.Increment(ref capture.sequence);
            var prefix = $"{number:0000}-{document}";
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            capture.Write(prefix + ".request.json", bytes);
            var attempt = ModelAttemptContext.Current;
            string stage = fallbackStage; string? unit = attempt?.Unit;
            try
            {
                using var outer = JsonDocument.Parse(bytes);
                var content = outer.RootElement.GetProperty("messages")[1].GetProperty("content");
                var text = content.ValueKind == JsonValueKind.Array ? content[0].GetProperty("text").GetString() : content.GetString();
                using var payload = JsonDocument.Parse(text!);
                if (payload.RootElement.TryGetProperty("stage", out var s)) stage = s.GetString() ?? fallbackStage;
                if (unit is null && payload.RootElement.TryGetProperty("unitId", out var u)) unit = u.GetString();
                else if (unit is null && payload.RootElement.TryGetProperty("chunk", out var chunk) && chunk.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                    unit = "chunk-" + chunk.ToString();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentNullException) { }
            try
            {
                var response = await base.SendAsync(request, ct);
                var raw = await response.Content.ReadAsByteArrayAsync(ct);
                capture.Write(prefix + ".response.json", raw);
                string? finishReason = null;
                void ReadFinish(string json)
                {
                    try
                    {
                        using var result = JsonDocument.Parse(json);
                        if (result.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                            foreach (var choice in choices.EnumerateArray())
                                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                                    finishReason = reason.GetString();
                    }
                    catch (JsonException) { }
                }
                var responseText = Encoding.UTF8.GetString(raw);
                var lines = responseText.Split('\n');
                if (lines.Any(line => line.StartsWith("data:", StringComparison.Ordinal)))
                {
                    foreach (var line in lines)
                        if (line.StartsWith("data:", StringComparison.Ordinal)) ReadFinish(line[5..].Trim());
                }
                else ReadFinish(responseText);
                var copy = new ByteArrayContent(raw);
                foreach (var h in response.Content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Content = copy;
                capture.Record(prefix + ".index.json", new { stage, unit, turn = attempt?.Turn, retry = attempt?.Retry, requestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    responseSha256 = Convert.ToHexStringLower(SHA256.HashData(raw)), status = (int)response.StatusCode, finishReason, responseAvailable = true });
                lock (capture.gate) Console.WriteLine($"Diagnostic call: document={document}, stage={stage}, unit={unit ?? "unknown"}, response=verified");
                return response;
            }
            catch
            {
                capture.Record(prefix + ".index.json", new { stage, unit, turn = attempt?.Turn, retry = attempt?.Retry, responseAvailable = false });
                throw;
            }
        }
    }
}
