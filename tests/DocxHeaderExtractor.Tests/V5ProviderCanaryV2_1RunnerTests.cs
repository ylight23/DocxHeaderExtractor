using System.Text.Json;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Infrastructure.AI;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The one place in the repository allowed to make a real OpenRouter call for the v2.1 wire-contract
/// canary - and only when a human has set <see cref="ExecuteSentinel"/> explicitly. Without it, this
/// test is a no-op pass, so the ordinary full suite stays entirely provider-free forever; V5CanaryGate
/// hard-pins the count to exactly three and still requires its own explicit authorization on top.
/// Never reads Gold and never scores heading F1 - the only questions here are wire-contract ones.
/// </summary>
public sealed class V5ProviderCanaryV2_1RunnerTests
{
    private const string ExecuteSentinel = "yes-i-understand-this-costs-money";
    private const string CanaryRoot = "artifacts/v5-provider-canary-v2_1";

    [Fact]
    public async Task Run_the_authorized_three_pack_canary_against_the_real_provider()
    {
        if (Environment.GetEnvironmentVariable("V5_CANARY_PROVIDER_EXECUTE") != ExecuteSentinel)
            return; // no-op: the ordinary suite never spends money or touches the network.

        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "Alibaba", "none", true, "json_object", 300)
        {
            UsageInclude = true,
        };
        var docs = new (string Id, string Pdf)[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .Select(item => (item.Id, Path: TestRepository.Path(item.Pdf),
                Built: V5PdfPreflightBuilder.BuildV2_1(TestRepository.Path(item.Pdf), item.Id, contract,
                    SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId, envelope)))
            .ToArray();

        var packA = (DocumentId: docs[0].Id, Pdf: docs[0].Path, Pack: docs[0].Built.Requests[0]);
        var packB = (DocumentId: docs[1].Id, Pdf: docs[1].Path, Pack: docs[1].Built.Requests[0]);
        var allPacks = docs.SelectMany(doc => doc.Built.Requests.Select(pack => (DocumentId: doc.Id, Pdf: doc.Path, Pack: pack))).ToArray();
        var byBytesDesc = allPacks
            .OrderByDescending(item => item.Pack.Request.Utf8Bytes)
            .ThenBy(item => item.DocumentId, StringComparer.Ordinal)
            .ThenBy(item => item.Pack.PackId, StringComparer.Ordinal)
            .ToArray();
        var packC = byBytesDesc.First(item =>
            !(item.DocumentId == packA.DocumentId && item.Pack.PackId == packA.Pack.PackId) &&
            !(item.DocumentId == packB.DocumentId && item.Pack.PackId == packB.Pack.PackId));
        var selection = new[] { packA, packB, packC };

        // Re-verifies against the frozen selection artifact this run must match byte for byte.
        var frozen = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CanaryRoot}/canary-selection.v1.json"))).RootElement;
        var frozenHashes = frozen.GetProperty("packs").EnumerateArray().Select(item => item.GetProperty("requestHash").GetString()).ToArray();
        Assert.Equal(frozenHashes, selection.Select(item => item.Pack.Request.RequestHash).ToArray());

        V5CanaryGate.Authorize(selection.Length, providerExecutionAuthorized: true);

        var options = RemoteInferenceOptions.FromEnvironment();
        var results = new List<object>();
        var passCount = 0;

        foreach (var item in selection)
        {
            var atoms = PdfStructuredSourceAuthorityBuilder.Build(item.Pdf).Atoms;
            var scope = ClaimBindingScope.Create(item.Pack.OwnedAliases, item.Pack.VisibleAliases);

            string? raw = null;
            string? transportError = null;
            try
            {
                using var client = OpenRouterHeaderExtractor.CreateOwned(options);
                raw = await client.BoundaryCutAsync(
                    "You are a task-defined semantic reasoner. Respond with a single JSON object matching the declared schema exactly.",
                    item.Pack.Request.Prompt,
                    expectedItemCount: item.Pack.OwnedAliases.Count);
            }
            catch (Exception ex)
            {
                transportError = ex.Message;
            }

            var transportValid = raw is not null;
            var jsonValid = false;
            var schemaValid = false;
            var groundingPresent = false;
            var vocabularyValid = false;
            var bindingValid = false;
            var ownershipValid = false;
            var claimCount = 0;
            var boundCount = 0;
            string? validationError = null;
            IReadOnlyDictionary<string, string> refusals = new Dictionary<string, string>();

            if (transportValid)
            {
                try
                {
                    using var document = JsonDocument.Parse(raw!);
                    jsonValid = true;
                    var response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);
                    schemaValid = true;
                    vocabularyValid = true; // enforced inside Parse -> Validate
                    claimCount = response.Claims.Count;
                    groundingPresent = claimCount == 0 || response.Claims.All(claim => claim.Subject.SourceParts.Count > 0);

                    var binding = ExactClaimBinderV2_1.Bind(item.Pack.PackId, response.Claims, atoms, scope);
                    refusals = binding.Refusals;
                    boundCount = binding.Bound.Count;
                    bindingValid = claimCount == 0 || boundCount > 0 || refusals.Count == 0;
                    ownershipValid = refusals.Values.All(reason =>
                        !reason.StartsWith("subject-alias-not-owned", StringComparison.Ordinal) &&
                        !reason.StartsWith("object-alias-not-visible", StringComparison.Ordinal));
                }
                catch (Exception ex)
                {
                    validationError = ex.Message;
                }
            }

            var passed = transportValid && jsonValid && schemaValid && groundingPresent && ownershipValid && bindingValid;
            if (passed) passCount++;

            results.Add(new
            {
                documentId = item.DocumentId,
                packId = item.Pack.PackId,
                requestHash = item.Pack.Request.RequestHash,
                transportValid,
                transportError,
                jsonValid,
                schemaValid,
                groundingPresent,
                vocabularyValid,
                bindingValid,
                ownershipValid,
                claimCount,
                boundCount,
                refusalCount = refusals.Count,
                refusalReasons = refusals.Values.Distinct().Take(10).ToArray(),
                validationError,
                rawResponseSha256 = raw is null ? null : Hashing.Sha256(raw),
                rawResponseChars = raw?.Length,
                pass = passed,
            });
        }

        var artifact = new
        {
            schemaVersion = "v5-provider-canary-result-v1",
            providerCalls = selection.Length,
            goldRead = false,
            headingF1Scored = false,
            providerExecutionAuthorized = true,
            model = envelope.Model,
            provider = envelope.Provider,
            passCount,
            total = selection.Length,
            overallPass = passCount == selection.Length,
            results,
        };
        var path = TestRepository.Path($"{CanaryRoot}/canary-result.v1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        Assert.Equal(selection.Length, passCount);
    }
}
