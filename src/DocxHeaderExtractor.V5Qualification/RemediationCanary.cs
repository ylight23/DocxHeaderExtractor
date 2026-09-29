using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.V5Qualification;

/// <summary>
/// Executes exactly the 3 provider requests frozen at commit 35f27fd
/// (<c>artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-selection.v1.json</c>),
/// under the source-selection wire policy fixed in that history. Not a re-selection: this mode never
/// picks its own packs, never opens the 31-pack cohort, never reads Gold, and never retries the model
/// semantically. It first rebuilds all three requests in-process and refuses to call a provider unless
/// every rebuilt <c>providerRequestHash</c>/<c>maxCompletionTokens</c> matches what was frozen -
/// environment parity by hash equality, the same standard <see cref="Cohort31"/> uses.
/// <list type="bullet">
/// <item><c>--remediation-canary</c> alone: provider-free plan/parity check only.</item>
/// <item><c>--remediation-canary --confirm-remediation-canary=...</c>: the only path that can call a
/// provider, and only after the parity check above passes. Writes the result exactly once - refuses
/// if the output file already exists.</item>
/// </list>
/// This is measurement: the report has no pass/fail threshold, only per-pack counts against the five
/// questions the canary was authorized to answer (alias-only usage, Class-B same-atom refusals,
/// Class-C multipart correctness, hard-negative refusals, runtimeProcessedSafely).
/// </summary>
internal static class RemediationCanary
{
    public const string ConfirmSentinel = "yes-i-authorize-exactly-3-frozen-remediation-canary-calls-35f27fd";
    private const string SelectionPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-selection.v1.json";
    private const string OutPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json";
    private const string Src089 = "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf";
    private const string Src095 = "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf";

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var confirm = args.FirstOrDefault(a => a.StartsWith("--confirm-remediation-canary=", StringComparison.Ordinal))?[("--confirm-remediation-canary=".Length)..];
        var authorized = confirm == ConfirmSentinel;

        var selectionPath = Path.Combine(root, SelectionPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(selectionPath)) return Fail($"remediation-canary: no frozen selection at {SelectionPath}");
        var selection = JsonNode.Parse(File.ReadAllText(selectionPath))!;
        var frozenPacks = selection["packs"]!.AsArray();
        if (frozenPacks.Count != 3) return Fail($"remediation-canary: frozen selection must have exactly 3 packs, found {frozenPacks.Count}");

        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
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

        var resolved = new List<(string DocumentId, string Role, string PdfPath, V5PackedSourceRequest Pack)>();
        foreach (var frozen in frozenPacks)
        {
            var documentId = frozen!["documentId"]!.GetValue<string>();
            var role = frozen["role"]!.GetValue<string>();
            var packId = frozen["packId"]!.GetValue<string>();
            var built = BuiltFor(documentId);
            var pack = built.Requests.SingleOrDefault(request => request.PackId == packId)
                ?? throw new InvalidOperationException($"pack-not-found-on-rebuild:{documentId}:{packId}");

            var frozenSemanticHash = frozen["newSemanticRequestHash"]!.GetValue<string>();
            var frozenProviderHash = frozen["newProviderRequestHash"]!.GetValue<string>();
            var frozenTokens = frozen["newMaxCompletionTokens"]!.GetValue<int>();
            if (pack.Request.RequestHash != frozenSemanticHash || pack.ProviderRequestHash != frozenProviderHash || pack.MaxCompletionTokens != frozenTokens)
                return Fail(
                    $"remediation-canary: rebuilt {documentId}:{packId} does not reproduce the frozen 35f27fd selection - " +
                    $"environment parity FAILED, no call made. frozenProviderHash={frozenProviderHash} rebuiltProviderHash={pack.ProviderRequestHash}");

            var pdfPath = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
            resolved.Add((documentId, role, pdfPath, pack));
        }

        Console.WriteLine("remediation-canary: environment parity PASS (all 3 requests reproduce the frozen 35f27fd hashes byte for byte)");
        foreach (var item in resolved)
            Console.WriteLine($"  [{item.Role}] {item.DocumentId} {item.Pack.PackId} providerRequestHash={item.Pack.ProviderRequestHash} maxCompletionTokens={item.Pack.MaxCompletionTokens}");

        if (!authorized)
        {
            Console.WriteLine();
            Console.WriteLine($"Not authorized (pass --confirm-remediation-canary={ConfirmSentinel}). providerCalls=0, goldRead=false.");
            return 0;
        }
        V5CanaryGate.Authorize(resolved.Count, authorized);

        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("OPENROUTER_API_KEY is not set. Refusing to run.");

        var outPath = Path.Combine(root, OutPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(outPath)) return Fail($"remediation-canary: {OutPath} already exists; this canary is executed exactly once");

        var baselineByPack = LoadOldCohortBaseline(root, resolved.Select(item => (item.DocumentId, item.Pack.PackId)));

        var results = new List<object>();
        var qualifications = new List<V5PackBindingQualification>();
        foreach (var item in resolved)
        {
            // Rebuild the frozen body one more time from the same deterministic inputs and require its
            // hash to match what was just verified - defense in depth, mirroring the original 3-pack
            // canary runner's own re-check immediately before the network call.
            var frozenBody = V5ProviderRequestBodyV2_1.Build(V5SystemPromptV2_1.Text, item.Pack.Request.Prompt, item.Pack.MaxCompletionTokens, envelope);
            if (!string.Equals(frozenBody.Hash, item.Pack.ProviderRequestHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"provider-request-hash-mismatch:{item.DocumentId}:{item.Pack.PackId}");

            var atoms = V5PdfPreflightBuilder.LoadAtoms(item.PdfPath);
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);

            string? raw = null;
            string? finishReason = null;
            string? transportError = null;
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(RemoteInferenceOptions.FromEnvironment());
                (raw, finishReason) = await client.ExecuteAsync(
                    frozenBody.PayloadBytes, item.Pack.MaxCompletionTokens, V5SystemPromptV2_1.Text, item.Pack.Request.Prompt);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }

            // No response repair, no semantic retry: exactly one attempt, classified with the same
            // codec/binder/scope DocumentAgentRuntime uses. A parse or binding failure is preserved as
            // a refusal, never patched and resent.
            var (qualification, _, _) = V5BindingQualifier.Qualify(raw, finishReason, transportError, contract, item.Pack.PackId, atoms, scope);
            qualifications.Add(qualification);

            var usage = raw is null ? null : AnalyzeSourceSelectionUsage(raw, contract, atoms, item.Pack.VisibleAliases.ToHashSet(StringComparer.Ordinal));
            var baseline = baselineByPack.GetValueOrDefault((item.DocumentId, item.Pack.PackId));

            results.Add(new
            {
                role = item.Role,
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                providerRequestHash = item.Pack.ProviderRequestHash,
                transportError,
                finishReason,
                rawResponseSha256 = raw is null ? null : Sha256(raw),
                rawResponseChars = raw?.Length,
                rawResponse = raw,
                qualification = qualification.ToReport(),
                sourceSelectionUsage = usage,
                oldCohortBaseline = baseline,
            });

            Console.WriteLine(
                $"  -> [{item.Role}] {item.DocumentId} {item.Pack.PackId}: {qualification.WireStatus} {qualification.Outcome} " +
                $"bound={qualification.BoundCount}/{qualification.ProposalCount} refused={qualification.RefusalCount} " +
                $"runtimeProcessedSafely={qualification.RuntimeProcessedSafely}");
        }

        var aggregate = V5QualificationAggregate.From(qualifications);
        var artifact = new
        {
            schemaVersion = "v5-source-selection-remediation-canary-result-v1",
            sourceSelectionFixCommit = "35f27fd",
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            providerCalls = resolved.Count,
            goldRead = false,
            headingF1Scored = false,
            semanticRetries = 0,
            responseRepairApplied = false,
            providerExecutionAuthorized = true,
            model = envelope.Model,
            provider = envelope.Provider,
            reasoning = envelope.Reasoning,
            purpose = "MEASUREMENT_NOT_PROMOTION - no 3/3 binding-complete threshold; the five questions this canary answers are in each result's sourceSelectionUsage/oldCohortBaseline and qualification.runtimeProcessedSafely",
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

    /// <summary>
    /// Provider-free classification of one raw response against the source-selection policy's five
    /// measurement questions: alias-only vs verbatimText usage, whether a same-atom (Class-B-style)
    /// exact-text refusal still occurs, whether multi-atom regions are represented as multiple
    /// sourceParts, and what the exact-text refusal categories were (to see hard negatives preserved).
    /// Never repairs or reinterprets the response; only reads it.
    /// </summary>
    private static object AnalyzeSourceSelectionUsage(
        string raw, DocumentTaskContract contract, IReadOnlyList<SemanticSourceAtom> atoms, IReadOnlySet<string> visibleAliases)
    {
        SemanticClaimResponseV2_1? response;
        try
        {
            using var document = JsonDocument.Parse(raw);
            response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);
        }
        catch (Exception)
        {
            return new { parsed = false };
        }

        var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
        int totalParts = 0, aliasOnlyParts = 0, verbatimTextParts = 0, multiPartEndpoints = 0;
        int successfulWholeAtomVerbatim = 0, successfulSubstringVerbatim = 0;
        int refusedSameAtomMechanical = 0, refusedTrueMultipart = 0;
        var hardNegativeCategories = new List<string>();

        foreach (var proposal in response.Claims)
        {
            foreach (var endpoint in new[] { proposal.Subject, proposal.Object })
            {
                if (endpoint is null) continue;
                if (endpoint.SourceParts.Count > 1) multiPartEndpoints++;
                foreach (var part in endpoint.SourceParts)
                {
                    totalParts++;
                    if (part.VerbatimText is null) { aliasOnlyParts++; continue; }
                    verbatimTextParts++;

                    var canonical = ProviderSourcePartNormalization.ToCanonical(part);
                    var binding = SemanticSourcePartBinder.Bind(atoms, [canonical]);
                    if (binding.IsBound)
                    {
                        var bound = binding.Parts[0];
                        var atomText = byAlias[part.SourceAlias].Text;
                        if (bound.Start == 0 && bound.End == atomText.Length) successfulWholeAtomVerbatim++;
                        else successfulSubstringVerbatim++;
                        continue;
                    }

                    var family = V5BindingQualifier.RefusalFamily(binding.Reason ?? binding.Status.ToString());
                    if (family != V5BindingQualifier.FamilyExactTextBinding) continue;

                    var diagnosis = V5ExactTextBindingAnalyzer.Analyze(
                        "remediation-canary", "remediation-canary", 0, proposal.Predicate,
                        ReferenceEquals(endpoint, proposal.Subject) ? "subject" : "object", 0,
                        part.SourceAlias, part.VerbatimText, part.Occurrence, part.LeftExactContext, part.RightExactContext,
                        binding.Reason ?? binding.Status.ToString(), atoms, visibleAliases);

                    if (diagnosis.Category == V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE) { refusedTrueMultipart++; continue; }
                    if (diagnosis.Counterfactuals.SameAtom.AnyMechanicalMatch) { refusedSameAtomMechanical++; continue; }
                    hardNegativeCategories.Add(diagnosis.Category.ToString());
                }
            }
        }

        return new
        {
            parsed = true,
            claimCount = response.Claims.Count,
            totalSourceParts = totalParts,
            aliasOnlyParts,
            verbatimTextParts,
            multiPartEndpoints,
            successfulWholeAtomVerbatim,
            successfulSubstringVerbatim,
            refusedSameAtomMechanical_ClassBStyle = refusedSameAtomMechanical,
            refusedTrueMultipart_ClassCStyle = refusedTrueMultipart,
            hardNegativeRefusals = hardNegativeCategories.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    /// <summary>The same three packs' old (pre-policy) frozen call, from the 31-pack cohort, for a direct before/after comparison.</summary>
    private static Dictionary<(string DocumentId, string PackId), object> LoadOldCohortBaseline(
        string root, IEnumerable<(string DocumentId, string PackId)> packs)
    {
        var cohortRoot = Path.Combine(root, "artifacts", "v5-provider-cohort-31-windows");
        var contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var cohort = JsonNode.Parse(File.ReadAllText(Path.Combine(cohortRoot, "preflight", "cohort.v1.json")))!;
        var atomsByDoc = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        var result = new Dictionary<(string, string), object>();

        foreach (var (documentId, packId) in packs)
        {
            var row = cohort["rows"]!.AsArray().Single(r => r!["DocumentId"]!.GetValue<string>() == documentId && r["PackId"]!.GetValue<string>() == packId)!;
            var visibleAliases = row["visibleAliases"]!.AsArray().Select(a => a!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            if (!atomsByDoc.TryGetValue(documentId, out var atoms))
            {
                var pdf = Path.Combine(root, (documentId == "SRC-089" ? Src089 : Src095).Replace('/', Path.DirectorySeparatorChar));
                atomsByDoc[documentId] = atoms = V5PdfPreflightBuilder.LoadAtoms(pdf);
            }

            var ordinal = row["Ordinal"]!.GetValue<int>();
            var callDirName = Directory.GetDirectories(Path.Combine(cohortRoot, "provider", "calls"))
                .Select(Path.GetFileName)
                .Single(name => name!.StartsWith($"{ordinal:00}-", StringComparison.Ordinal));
            var contentPath = Path.Combine(cohortRoot, "provider", "calls", callDirName!, "content.txt");
            if (!File.Exists(contentPath)) continue;
            var content = File.ReadAllText(contentPath);
            result[(documentId, packId)] = AnalyzeSourceSelectionUsage(content, contract, atoms, visibleAliases);
        }
        return result;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
