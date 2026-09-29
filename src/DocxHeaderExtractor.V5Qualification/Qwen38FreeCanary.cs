using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Executes exactly the 3 strict-schema requests frozen by the offline preflight test
/// (<c>artifacts/qwen38-27b-free-3pack-canary/preflight.json</c>) against qwen/qwen3.8-27b:free -
/// the only endpoint for that slug (ModelRun). Rebuilds all three bodies in-process first and refuses
/// to call a provider unless every rebuilt hash matches what preflight froze. Never picks its own
/// packs, never opens Gold, never retries semantically, never repairs a response.
/// <list type="bullet">
/// <item><c>--qwen38-free-canary</c> alone: provider-free plan/parity check only.</item>
/// <item><c>--qwen38-free-canary --confirm-qwen38-free-canary=...</c>: the only path that can call a
/// provider, and only after the parity check passes. Writes the result exactly once.</item>
/// </list>
/// </summary>
internal static class Qwen38FreeCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-3-frozen-qwen38-27b-free-modelrun-calls";
    private const string ArtifactRoot = "artifacts/qwen38-27b-free-3pack-canary";
    private const string Model = "qwen/qwen3.8-27b:free";
    private const string ProviderRoutingSlug = "modelrun";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm-qwen38-free-canary=", StringComparison.Ordinal))?[("--confirm-qwen38-free-canary=".Length)..];
        var authorized = confirm == ConfirmSentinel;

        var preflightPath = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), "preflight.json");
        if (!File.Exists(preflightPath)) return Fail($"qwen38-free-canary: no frozen preflight at {ArtifactRoot}/preflight.json");
        var preflight = JsonNode.Parse(File.ReadAllText(preflightPath))!;
        var frozenPacks = preflight["packs"]!.AsArray();
        if (frozenPacks.Count != 3) return Fail($"qwen38-free-canary: frozen preflight must have exactly 3 packs, found {frozenPacks.Count}");
        if (preflight["model"]!.GetValue<string>() != Model)
            return Fail($"qwen38-free-canary: frozen preflight names model {preflight["model"]}, expected {Model}");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope(Model, ProviderRoutingSlug, "none", true, "json_schema", 300) { UsageInclude = true };
        var schemaName = SemanticClaimContractV2_1.SchemaVersion.Replace('-', '_').Replace('.', '_');
        var builtByDoc = new Dictionary<string, (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests)>(StringComparer.Ordinal);
        (V5ProviderPreflight Preflight, IReadOnlyList<V5PackedSourceRequest> Requests) BuiltFor(string documentId)
        {
            if (!builtByDoc.TryGetValue(documentId, out var built))
            {
                var pdf = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
                built = V5PdfPreflightBuilder.BuildV2_1(pdf, documentId, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope);
                builtByDoc[documentId] = built;
            }
            return built;
        }

        var resolved = new List<(string Role, string DocumentId, string PdfPath, V5PackedSourceRequest Pack, byte[] FrozenBody, string FrozenHash)>();
        foreach (var frozen in frozenPacks)
        {
            var role = frozen!["role"]!.GetValue<string>();
            var documentId = frozen["documentId"]!.GetValue<string>();
            var packId = frozen["packId"]!.GetValue<string>();
            var built = BuiltFor(documentId);
            var pack = built.Requests.SingleOrDefault(request => request.PackId == packId)
                ?? throw new InvalidOperationException($"pack-not-found-on-rebuild:{documentId}:{packId}");

            var compiledSchema = V5StrictClaimSchemaCompilerV1.Compile(contract, pack.OwnedAliases, pack.VisibleAliases);
            var body = V5StrictSchemaProviderRequestBodyV1.Build(
                V5SystemPromptV2_1.Text, pack.Request.Prompt, pack.MaxCompletionTokens, Model, ProviderRoutingSlug, compiledSchema, schemaName, "none");

            var frozenHash = frozen["providerRequestHash"]!.GetValue<string>();
            if (body.Hash != frozenHash)
                return Fail(
                    $"qwen38-free-canary: rebuilt {documentId}:{packId} does not reproduce the frozen preflight - " +
                    $"environment parity FAILED, no call made. frozenHash={frozenHash} rebuiltHash={body.Hash}");

            var bodyFilePath = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), "bodies", $"{documentId}-{packId[(packId.LastIndexOf(':') + 1)..]}.json");
            var frozenBodyBytes = File.ReadAllBytes(bodyFilePath);
            if (!frozenBodyBytes.AsSpan().SequenceEqual(body.PayloadBytes))
                return Fail($"qwen38-free-canary: frozen body file for {documentId}:{packId} does not match the rebuilt bytes - no call made");

            var pdfPath = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
            resolved.Add((role, documentId, pdfPath, pack, frozenBodyBytes, frozenHash));
        }

        Console.WriteLine("qwen38-free-canary: environment parity PASS (all 3 requests reproduce the frozen preflight hashes byte for byte)");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Role}] {item.DocumentId} {item.Pack.PackId} providerRequestHash={item.FrozenHash}");

        if (!authorized)
        {
            Console.WriteLine();
            Console.WriteLine($"Not authorized (pass --confirm-qwen38-free-canary={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }
        V5CanaryGate.Authorize(resolved.Count, authorized);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("OPENROUTER_API_KEY is not set. Refusing to run.");

        var outPath = Path.Combine(root, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar), "canary-result.v1.json");
        if (File.Exists(outPath)) return Fail($"qwen38-free-canary: {ArtifactRoot}/canary-result.v1.json already exists; this canary is executed exactly once");

        var results = new List<object>();
        var qualifications = new List<V5PackBindingQualification>();
        foreach (var item in resolved)
        {
            var atoms = V5PdfPreflightBuilder.LoadAtoms(item.PdfPath);
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);

            string? raw = null;
            string? finishReason = null;
            string? transportError = null;
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(RemoteInferenceOptions.FromEnvironment());
                (raw, finishReason) = await client.ExecuteAsync(
                    item.FrozenBody, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Pack.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }

            var (qualification, _, _) = V5BindingQualifier.Qualify(raw, finishReason, transportError, contract, item.Pack.PackId, atoms, scope);
            qualifications.Add(qualification);

            results.Add(new
            {
                role = item.Role,
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                providerRequestHash = item.FrozenHash,
                transportError,
                finishReason,
                rawResponseSha256 = raw is null ? null : Sha256(raw),
                rawResponseChars = raw?.Length,
                rawResponse = raw,
                qualification = qualification.ToReport(),
            });

            Console.WriteLine(
                $"  -> [{item.Role}] {item.DocumentId} {item.Pack.PackId}: {qualification.WireStatus} {qualification.Outcome} " +
                $"bound={qualification.BoundCount}/{qualification.ProposalCount} refused={qualification.RefusalCount} " +
                $"runtimeProcessedSafely={qualification.RuntimeProcessedSafely}");
        }

        var aggregate = V5QualificationAggregate.From(qualifications);
        var artifact = new
        {
            schemaVersion = "v5-qwen38-27b-free-canary-result-v1",
            purpose = "MEASUREMENT_NOT_PROMOTION",
            model = Model,
            actualProviderRoute = ProviderRoutingSlug,
            responseFormatType = "json_schema",
            strict = true,
            reasoningEffort = "none",
            providerCalls = resolved.Count,
            goldRead = false,
            semanticRetries = 0,
            responseRepairApplied = false,
            providerExecutionAuthorized = true,
            aggregate,
            results,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine(
            $"usable={aggregate.UsableResponses}/{aggregate.ProviderCalls} bound={aggregate.TotalBound}/{aggregate.TotalProposals} " +
            $"runtimeProcessedSafely={aggregate.PacksRuntimeProcessedSafely}/{aggregate.ProviderCalls}; wrote {outPath}");
        return 0;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
