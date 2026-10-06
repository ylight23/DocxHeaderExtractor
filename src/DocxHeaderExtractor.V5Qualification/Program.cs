using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// The only place a real V5 provider call can originate. Not a test project - <c>dotnet test</c>
/// never discovers or runs an Exe project, so the ordinary suite stays provider-free regardless of
/// environment variables. A real call additionally requires <c>--confirm=yes-i-understand-this-costs-money</c>
/// on the command line and <c>OPENROUTER_API_KEY</c> in the environment; without both this prints the
/// plan and exits without opening a socket. <see cref="V5CanaryGate"/> hard-pins the request count to
/// three regardless. Never reads Gold, never scores heading F1.
/// </summary>
internal static class Program
{
    private const string ConfirmSentinel = "yes-i-understand-this-costs-money";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    private static async Task<int> Main(string[] args)
    {
        // P6T-A is a two-call, total anchor-role ledger viability canary. It never scores semantics;
        // execution needs its own explicit sentinel and makes no retry, repair, fallback, or Pass-2 call.
        if (args.Contains("--p6ta-total-anchor-role-canary"))
            return await P6TATotalAnchorRoleCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tc-correspondence-evidence-canary"))
            return await P6TCCorrespondenceEvidenceCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6td-explicit-abstention-canary"))
            return await P6TDExplicitAbstentionCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te1-unit-topology-canary"))
            return await P6TE1UnitTopologyCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tf1-function-membership-canary"))
            return await P6TF1FunctionMembershipCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te-doc0256-e-challenge") || args.Contains("--p6te-doc0252-e-challenge") || args.Contains("--p6te-src041-e-challenge"))
            return await P6TEChallengeCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6te-src041-h2-challenge"))
            return await P6TEH2ContinuationCanary.RunAsync(LocateRepoRoot(), args);
        // P6T-G2A population executes only the five requests frozen across the complete F1-authority cohort.
        if (args.Contains("--p6tg2a-full-pack-population"))
            return await P6TG2AFullPackPopulationCanary.RunAsync(LocateRepoRoot(), args);
        // P6T-H2 executes only the 31 anchor-scoped edge ledgers frozen after full-pack G2A.
        if (args.Contains("--p6th3-full-population-h2"))
            return await P6TH3FullPopulationH2Canary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th-h2b1-k4-full-population"))
            return await P6TH3FullPopulationH2Canary.RunH2B1Async(LocateRepoRoot(), args);
        // P6T-H2C executes the frozen direct end-pointer requests over the full G2A-HAS population.
        if (args.Contains("--p6th2c-direct-end-pointer-full31"))
            return await P6TH2CEndPointerCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-paired"))
            return await P6TH2CEndPointerCanary.RunCleanPairedAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-v2"))
            return await P6TH2CEndPointerCanary.RunCleanV2OnlyAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-clean-v1"))
            return await P6TH2CEndPointerCanary.RunCleanV1OnlyAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th2c-freeze-response-hashes"))
            return P6TH2CEndPointerCanary.FreezeCaptureHashes(LocateRepoRoot());
        if (args.Contains("--p6th2c-clarified-retry-until-accepted"))
            return await P6TH2CEndPointerCanary.ClarifiedRetryUntilAcceptedAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6th3-conflict-adjudication"))
            return await P6TH3ConflictAdjudicationCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6tf1-retry-src089"))
            return await P6TF1FunctionMembershipCanary.RetrySrc089Async(LocateRepoRoot(), args);

        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm=", StringComparison.Ordinal))?[10..];
        var authorized = confirm == ConfirmSentinel;

        var root = LocateRepoRoot();
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        {
            UsageInclude = true,
        };

        var docs = new (string Id, string Path)[]
        {
            ("SRC-089", Path.Combine(root, Src089.Replace('/', Path.DirectorySeparatorChar))),
            ("SRC-095", Path.Combine(root, Src095.Replace('/', Path.DirectorySeparatorChar))),
        };
        var built = docs
            .Select(doc => (doc.Id, doc.Path, Built: V5PdfPreflightBuilder.BuildV2_1(
                doc.Path, doc.Id, contract, V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, envelope)))
            .ToArray();

        var packA = (DocumentId: built[0].Id, Pdf: built[0].Path, Pack: built[0].Built.Requests[0]);
        var packB = (DocumentId: built[1].Id, Pdf: built[1].Path, Pack: built[1].Built.Requests[0]);
        var allPacks = built.SelectMany(doc => doc.Built.Requests.Select(pack => (DocumentId: doc.Id, Pdf: doc.Path, Pack: pack))).ToArray();
        var byBytesDesc = allPacks
            .OrderByDescending(item => item.Pack.Request.Utf8Bytes)
            .ThenBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.Pack.PackId, StringComparer.Ordinal)
            .ToArray();
        var packC = byBytesDesc.First(item =>
            !(item.DocumentId == packA.DocumentId && item.Pack.PackId == packA.Pack.PackId) &&
            !(item.DocumentId == packB.DocumentId && item.Pack.PackId == packB.Pack.PackId));
        var selection = new[] { packA, packB, packC };

        Console.WriteLine($"V5 canary qualification runner - protocol {V5Protocol.ClaimSchemaVersionV2_1}, composer {V5SemanticRequestComposerV2_1.Version}");
        Console.WriteLine($"HEAD: {GitHead(root)}");
        foreach (var item in selection)
            Console.WriteLine($"  {item.DocumentId} {item.Pack.PackId} bytes={item.Pack.Request.Utf8Bytes} maxCompletionTokens={item.Pack.MaxCompletionTokens} providerRequestHash={item.Pack.ProviderRequestHash}");

        if (!authorized)
        {
            Console.WriteLine();
            Console.WriteLine("Not authorized (pass --confirm=yes-i-understand-this-costs-money to execute).");
            Console.WriteLine("Plan only - no provider call, no network access. providerCalls=0, goldRead=false.");
            return 0;
        }
        // Defense in depth even once the flag above is set: the gate still refuses anything but
        // exactly three requests, so a bug upstream in pack selection can never over-execute.
        V5CanaryGate.Authorize(selection.Length, authorized);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("OPENROUTER_API_KEY is not set. Refusing to run.");
            return 1;
        }

        var options = RemoteInferenceOptions.FromEnvironment();
        var results = new List<object>();
        var qualifications = new List<V5PackBindingQualification>();

        foreach (var item in selection)
        {
            // Rebuild the frozen body from the same deterministic inputs preflight used, and require
            // its hash to match what preflight recorded. A mismatch here means execution would send
            // something other than what was frozen, and must fail closed rather than send it anyway.
            var frozenBody = V5ProviderRequestBodyV2_1.Build(
                V5SystemPromptV2_1.Text, item.Pack.Request.Prompt, item.Pack.MaxCompletionTokens, envelope);
            if (!string.Equals(frozenBody.Hash, item.Pack.ProviderRequestHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"provider-request-hash-mismatch:{item.DocumentId}:{item.Pack.PackId}:frozen={frozenBody.Hash}:preflight={item.Pack.ProviderRequestHash}");

            var atoms = V5PdfPreflightBuilder.LoadAtoms(item.Pdf);
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);

            string? raw = null;
            string? finishReason = null;
            string? transportError = null;
            try
            {
                using var client = OpenRouterQualificationTransport.CreateOwned(options);
                (raw, finishReason) = await client.ExecuteAsync(
                    frozenBody.PayloadBytes, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Pack.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }

            // The same codec and binder DocumentAgentRuntime uses, classified with the runtime's own
            // partial fail-closed semantics: only a response-fatal problem discards the response; a
            // claim-local refusal discards that one proposal, is preserved explicitly, and keeps its
            // valid siblings. See V5PackBindingQualification.
            var (qualification, _, _) = V5BindingQualifier.Qualify(
                raw, finishReason, transportError, contract, item.Pack.PackId, atoms, scope);
            qualifications.Add(qualification);
            object[]? diagnosticClaimTable = null;
            if (raw is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    diagnosticClaimTable = BuildDiagnosticClaimTable(document.RootElement, contract);
                }
                catch (JsonException)
                {
                }
            }

            results.Add(new
            {
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                providerRequestHash = item.Pack.ProviderRequestHash,
                transportError,
                finishReason,
                rawResponseSha256 = raw is null ? null : Sha256(raw),
                rawResponseChars = raw?.Length,
                rawResponse = raw,
                diagnosticClaimTable,
                qualification = qualification.ToReport(),
            });

            Console.WriteLine(
                $"  -> {item.DocumentId} {item.Pack.PackId}: {qualification.WireStatus} {qualification.Outcome} " +
                $"bound={qualification.BoundCount}/{qualification.ProposalCount} refused={qualification.RefusalCount} " +
                $"runtimeProcessedSafely={qualification.RuntimeProcessedSafely}");
        }

        var aggregate = V5QualificationAggregate.From(qualifications);
        var artifact = new
        {
            schemaVersion = "v5-provider-canary-result-v2",
            head = GitHead(root),
            providerCalls = selection.Length,
            goldRead = false,
            headingF1Scored = false,
            providerExecutionAuthorized = true,
            model = envelope.Model,
            provider = envelope.Provider,
            aggregate,
            results,
        };
        // v2 of the result file: never overwrites the frozen v1 raw-response artifact.
        var outPath = Path.Combine(root, "artifacts", "v5-provider-canary-current", "canary-result.v2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine(
            $"usable={aggregate.UsableResponses}/{aggregate.ProviderCalls} bound={aggregate.TotalBound}/{aggregate.TotalProposals} " +
            $"complete={aggregate.PacksBindingComplete} partial={aggregate.PacksPartialBinding} empty={aggregate.PacksBindingEmpty}; wrote {outPath}");
        // Measurement, not promotion: the exit code reflects only whether every response was usable
        // and safely processed, never a refusal threshold.
        return aggregate.UsableResponses == selection.Length && aggregate.PacksRuntimeProcessedSafely == selection.Length ? 0 : 1;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocxHeaderExtractor.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException(
            $"No DocxHeaderExtractor.sln above {AppContext.BaseDirectory}");
    }

    private static string? GitHead(string root)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            var output = process!.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Diagnostic only - never governs pass/fail. Groups whatever the provider actually sent by
    /// predicate so a rejected response's arity pattern is still inspectable afterward, without a
    /// second provider call. Deliberately more lenient than <see cref="SemanticClaimResponseCodecV2_1"/>:
    /// it must still produce a table for a response the strict codec refuses.
    /// </summary>
    private static object[]? BuildDiagnosticClaimTable(JsonElement payload, DocumentTaskContract contract)
    {
        try
        {
            if (!payload.TryGetProperty("claims", out var claimsElement) || claimsElement.ValueKind != JsonValueKind.Array)
                return null;
            var raw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(claimsElement.GetRawText());
            if (raw is null) return null;

            var relationNames = contract.Relations.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            var predicateNames = contract.Predicates.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);

            var flattened = raw.Select(claim => new
            {
                predicate = claim.TryGetValue("predicate", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                state = claim.TryGetValue("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                hasValue = claim.ContainsKey("value"),
                hasObject = claim.ContainsKey("object"),
            }).ToArray();

            return flattened
                .GroupBy(claim => claim.predicate ?? "(missing)")
                .Select(group => (object)new
                {
                    predicate = group.Key,
                    declaredKind = relationNames.Contains(group.Key) ? "RELATION" : predicateNames.Contains(group.Key) ? "UNARY" : "UNKNOWN",
                    claimCount = group.Count(),
                    withValue = group.Count(claim => claim.hasValue),
                    withObject = group.Count(claim => claim.hasObject),
                    withoutObject = group.Count(claim => !claim.hasObject),
                    stateDistribution = group.GroupBy(claim => claim.state ?? "(missing)")
                        .ToDictionary(stateGroup => stateGroup.Key, stateGroup => stateGroup.Count()),
                })
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}
