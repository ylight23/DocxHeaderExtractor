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
        // The 31-pack measurement cohort has its own modes, gate and sentinel; see Cohort31.
        if (args.Any(a => a.StartsWith("--cohort31-", StringComparison.Ordinal)))
            return await Cohort31.RunAsync(LocateRepoRoot(), args);

        // The frozen 3-pack source-selection remediation canary has its own gate and sentinel; see RemediationCanary.
        if (args.Contains("--remediation-canary"))
            return await RemediationCanary.RunAsync(LocateRepoRoot(), args);

        // The frozen qwen3.8-27b:free strict-schema model-capability canary has its own gate and sentinel; see Qwen38FreeCanary.
        if (args.Contains("--qwen38-free-canary"))
            return await Qwen38FreeCanary.RunAsync(LocateRepoRoot(), args);

        // The frozen qwen3.7-flash forced-tool-call Variant A canary has its own gate and sentinel; see Qwen37ForcedToolCanary.
        if (args.Contains("--qwen37-forced-tool"))
            return await Qwen37ForcedToolCanary.RunAsync(LocateRepoRoot(), args);

        // The frozen qwen3.7-flash forced-tool-call Variant A2 (tool_choice=required) single-pack canary; see Qwen37ForcedToolVariantA2Canary.
        if (args.Contains("--qwen37-forced-tool-a2"))
            return await Qwen37ForcedToolVariantA2Canary.RunAsync(LocateRepoRoot(), args);

        // P3 is the one-call ToolAuto capability canary. Its distinct gate hard-pins PACK_006 and
        // disables every transport retry; see Qwen37ToolAutoCanary.
        if (args.Contains("--qwen37-tool-auto"))
            return await Qwen37ToolAutoCanary.RunAsync(LocateRepoRoot(), args);

        // P5F is the bounded four-pack v3 wire-compliance canary. It has an independent gate and
        // never opens the historical 31-pack cohort; see P5FV3Canary.
        if (args.Contains("--p5f-v3-canary"))
            return await P5FV3Canary.RunAsync(LocateRepoRoot(), args);

        // P5F2 is the post-P5G replacement four-pack canary. Its runner rejects any body that
        // does not reproduce the P5H manifest byte-for-byte before an explicit call authorization.
        if (args.Contains("--p5f2-v3-canary"))
            return await P5F2V3Canary.RunAsync(LocateRepoRoot(), args);

        // P5K executes only the four P5J-frozen 32-decision shard bodies. It has its own
        // authorization token, repeat-result guard and no-retry transport policy.
        if (args.Contains("--p5k-v3-sharded-canary"))
            return await P5KV3ShardedCanary.RunAsync(LocateRepoRoot(), args);

        if (args.Contains("--p5m-v31-sparse-canary"))
            return await P5MSparseCanary.RunAsync(LocateRepoRoot(), args);

        if (args.Contains("--p5o-v32-semantic-cohort"))
            return await P5OV32SemanticCohort.RunAsync(LocateRepoRoot(), args);

        if (args.Contains("--p5r-v32-full31"))
            return await P5RFull31V32Execution.RunAsync(LocateRepoRoot(), args);

        // P5V is a V3.3 qualification-only lane. Its frozen manifest currently carries the P5U
        // volume block, so the runner proves body parity then stops before network.
        if (args.Contains("--p5v-v33-full31"))
            return await P5VCompactV33Qualification.RunAsync(LocateRepoRoot(), args);

        // P6K is a four-call, compact sparse-locator qualification canary. It has its own
        // prepared manifest, one-attempt transport policy and repeat-result guard.
        if (args.Contains("--p6k-compact-sparse-canary"))
            return await P6KCompactSparseCanary.RunAsync(LocateRepoRoot(), args);

        // P6L replays the same four frozen packs with only the canonical locator grammar clarified.
        if (args.Contains("--p6l-canonical-locator-canary"))
            return await P6KCompactSparseCanary.RunClarifiedAsync(LocateRepoRoot(), args);

        // P6M executes one frozen P6L clarified request per each of the 31 original parent packs.
        if (args.Contains("--p6m-p6l-full31"))
            return await P6MFull31CanonicalLocatorQualification.RunAsync(LocateRepoRoot(), args);

        // P6N is an ontology-free, free-reasoning heading ceiling test on the same frozen four P6L packs.
        // Preparing request hashes is provider-free; execution has its own explicit four-call sentinel.
        if (args.Contains("--p6n-free-reasoning-canary"))
            return await P6NFreeReasoningHeadingCanary.RunAsync(LocateRepoRoot(), args);

        // Changed P6N arm: no response_format or harness-defined output schema; it requires a distinct manifest and approval.
        if (args.Contains("--p6n-unconstrained-output-canary"))
            return await P6NUnschematizedHeadingCanary.RunAsync(LocateRepoRoot(), args);

        // P6N-B removes the semantic ontology but keeps an exact sourceParts locator contract for bindable headings.
        if (args.Contains("--p6nb-bound-locator-canary"))
            return await P6NBBoundLocatorCanary.RunAsync(LocateRepoRoot(), args);

        // Full-31 continuation of the same P6N-B free-semantic + exact-locator treatment, one primary attempt per P05 pack.
        if (args.Contains("--p6nb-bound-locator-full31"))
            return await P6NBBoundLocatorFull31.RunAsync(LocateRepoRoot(), args);

        // One separately authorized, exact-body retry for the sole P6N-B full31 length response.
        if (args.Contains("--p6nb-full31-pack007-retry"))
            return await P6NBBoundLocatorFull31.RetryPack007Async(LocateRepoRoot(), args);

        // P6N-C full31: P6N-B free semantics/locator with only the separately frozen generic boundary cues appended.
        if (args.Contains("--p6nc-semantic-boundary-full31"))
            return await P6NCSemanticBoundaryFull31.RunAsync(LocateRepoRoot(), args);

        // P6P freezes and executes the PDF production-candidate adapter; execution has a separate explicit sentinel.
        if (args.Contains("--p6p-document-aware-pdf-full31"))
            return await P6PDocumentAwarePdfQualification.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6pl-layout-aware-pdf-full31"))
            return await P6PDocumentAwarePdfQualification.RunLayoutAwareAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6p-freeze-response-hashes"))
            return P6PDocumentAwarePdfQualification.FreezeResponseHashes(LocateRepoRoot());
        if (args.Contains("--p6pl-freeze-response-hashes"))
            return P6PDocumentAwarePdfQualification.FreezeLayoutAwareResponseHashes(LocateRepoRoot());
        if (args.Contains("--p6p-rerun-failed-pack"))
            return await P6PDocumentAwarePdfQualification.RerunFailedPackAsync(LocateRepoRoot(), args);

        // P6S-C executes only four snapshot-replayed candidate-authority bodies.  It has its own
        // manifest, explicit sentinel, zero retry policy, and leaves Gold unread for offline scoring.
        if (args.Contains("--p6sc-candidate-authority-canary"))
            return await P6SCandidateAuthorityCanary.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6sd-candidate-authority-full31"))
            return await P6SCandidateAuthorityFull31.RunAsync(LocateRepoRoot(), args);

        // P6S-R is the matched causal follow-up to P6S-D: reasoning is its sole independent variable.
        if (args.Contains("--p6sr-matched-reasoning-full31"))
            return await P6SRMatchedReasoningFull31.RunAsync(LocateRepoRoot(), args);

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
        if (args.Contains("--p6tf1-retry-src089"))
            return await P6TF1FunctionMembershipCanary.RetrySrc089Async(LocateRepoRoot(), args);

        // P6S-I/J are separate one-pack neutral-context and semantic-wording experiments; their
        // runner has an independent two-call cap, frozen-body manifest, and zero-retry policy.
        if (args.Contains("--p6sij-toc-two-arm-experiment"))
            return await P6SIJTwoArmTocExperiment.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6sij-score"))
            return P6SIJTwoArmTocExperiment.Score(LocateRepoRoot());

        // P6R adds only deterministic source-correspondence evidence and a representation result
        // channel to the text-only P6P treatment. It is qualification-only and never reads Gold.
        if (args.Contains("--p6r-structural-identity-full31"))
            return await P6RStructuralIdentityResolutionQualification.RunAsync(LocateRepoRoot(), args);
        if (args.Contains("--p6r-freeze-response-hashes"))
            return P6RStructuralIdentityResolutionQualification.FreezeResponseHashes(LocateRepoRoot());
        if (args.Contains("--p6r-retry-contract-invalid-packs"))
            return await P6RStructuralIdentityResolutionQualification.RetryContractInvalidPacksAsync(LocateRepoRoot(), args);

        // Separately authorized, exact-body-only retry budget for the failed fourth P6N-B canary request.
        if (args.Contains("--p6nb-bound-locator-call4-retry"))
            return await P6NBBoundLocatorCanary.RetryFourthAsync(LocateRepoRoot(), args);

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
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
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
