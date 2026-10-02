using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6LCanonicalLocatorContractTests
{
    private const string Root = "artifacts/v5-p6l-canonical-locator-contract";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const string P6KRoot = "artifacts/v5-p6k-compact-sparse-canary";
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Role, string Doc, string Pdf, int Ordinal)[] Roles =
    [
        ("MAX_REQUEST_BODY", "SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", 4),
        ("L1472_OWNER_OMISSION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 17),
        ("L1710_RETYPING", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 20),
        ("MULTIPART_RELATION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 11),
    ];

    [Fact]
    public void Freeze_P6L_four_pack_contract_only_delta_and_prepared_manifest()
    {
        var repo = TestRepository.Root();
        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        var p6k = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6KRoot, "result.v1.json")))!.AsObject();
        Assert.Equal(4, p6k["providerCalls"]!.GetValue<int>());
        Assert.False(p6k["goldRead"]!.GetValue<bool>());
        var priorRows = p6k["rows"]!.AsArray();
        var cache = new Dictionary<string, (string Pdf, IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        var manifestRows = new List<object>();
        var diagnosisRows = new List<object>();

        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.Doc, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (pdf, V5PdfPreflightBuilder.BuildV3(pdf, role.Doc, Contract,
                        V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(role.Doc, source);
            }
            var pack = source.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.Ordinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var old = V5SparseCandidateRequestComposerV1.ComposeCompactDirectory(Contract, pack.Packet, registry);
            var clarified = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var oldUser = JsonNode.Parse(old.UserMessage)!.AsObject();
            var newUser = JsonNode.Parse(clarified.UserMessage)!.AsObject();
            oldUser.Remove("protocolVersion"); oldUser.Remove("responseContract");
            newUser.Remove("protocolVersion"); newUser.Remove("responseContract"); newUser.Remove("locatorCanonicalRule");
            Assert.True(JsonNode.DeepEquals(oldUser, newUser), $"non-contract request content changed for {role.Role}");
            Assert.Contains("WHOLE ATOM (canonical)", clarified.SystemPrompt, StringComparison.Ordinal);
            Assert.Contains("full-span pair", clarified.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("oneOf", clarified.UserMessage, StringComparison.Ordinal);
            Assert.Contains("wholeAtom", clarified.UserMessage, StringComparison.Ordinal);

            var oldBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(old.SystemPrompt, old.UserMessage, pack.MaxCompletionTokens, Envelope);
            var newBody = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(clarified.SystemPrompt, clarified.UserMessage, pack.MaxCompletionTokens, Envelope);
            using var oldBodyJson = JsonDocument.Parse(oldBody.PayloadBytes);
            using var newBodyJson = JsonDocument.Parse(newBody.PayloadBytes);
            var oldProperties = oldBodyJson.RootElement.EnumerateObject().Where(property => property.Name != "messages").ToArray();
            foreach (var property in oldProperties)
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(property.Value.GetRawText()), JsonNode.Parse(newBodyJson.RootElement.GetProperty(property.Name).GetRawText())),
                    $"provider carrier field {property.Name} changed for {role.Role}");

            var p6iRow = p6i["rows"]!.AsArray().Single(row => row!["DocumentId"]!.GetValue<string>() == role.Doc && row["ParentOrdinal"]!.GetValue<int>() == role.Ordinal)!;
            Assert.Equal(p6iRow["CompactProviderBodyBytes"]!.GetValue<int>(), oldBody.Bytes);
            Assert.Equal(p6iRow["CompactProviderBodySha256"]!.GetValue<string>(), oldBody.Hash);
            var p6kRow = priorRows.Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            Assert.Equal(oldBody.Hash, p6kRow["providerRequestHash"]!.GetValue<string>());
            var output = JsonDocument.Parse(p6kRow["rawResponse"]!.GetValue<string>());
            var candidates = output.RootElement.GetProperty("occurrences").EnumerateArray().ToArray();
            var candidateCount = candidates.Length;
            var allCandidateReasons = p6kRow["analysis"]!["quarantines"]!.AsArray()
                .Select(item => item!["Reason"]!.GetValue<string>()).ToArray();
            Assert.Equal(candidateCount, allCandidateReasons.Length);
            Assert.All(allCandidateReasons, reason => Assert.Equal("locator-whole-must-omit-boundaries", reason));
            diagnosisRows.Add(new
            {
                role = role.Role, finishReason = p6kRow["finishReason"]!.GetValue<string>(),
                actualPromptTokens = p6kRow["usage"]!["prompt_tokens"]!.GetValue<int>(),
                proposedOccurrences = candidateCount, boundOccurrences = p6kRow["analysis"]!["bound"]!.GetValue<int>(),
                quarantineReasons = allCandidateReasons,
            });
            manifestRows.Add(new
            {
                role = role.Role, documentId = role.Doc, packId = pack.PackId, ownedAtoms = pack.OwnedAliases.Count,
                semanticRequestHash = clarified.UserMessageSha256, registryFingerprint = registry.Fingerprint,
                providerRequestHash = newBody.Hash, providerRequestBytes = newBody.Bytes,
                maxCompletionTokens = pack.MaxCompletionTokens, maxResponseUtf8Bytes = 49152,
                p6kBaselineProviderRequestHash = oldBody.Hash,
            });
        }

        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p6l-canonical-locator-contract-audit-v1", providerCalls = 0,
            p6kSourceProviderCalls = p6k["providerCalls"]!.GetValue<int>(), goldRead = false, goldMutation = "NONE",
            finding = new
            {
                priorSystemPromptPrimaryExample = "used atom plus from/to, while whole atom was only prose",
                priorResponseContract = "field-name lists without an exclusive whole-vs-substring locator union",
                priorDirectoryBoundaryOrdering = "documented, but canonical first-to-final full-span prohibition was not adjacent to response grammar",
                p6kOutcome = "all 16 candidates across four calls were quarantined as locator-whole-must-omit-boundaries",
            },
            contractDelta = "only protocol version, system instructions, and response-contract description changed; task, source/context payload, locator directory, route, carrier, token budget, parser, binder unchanged",
            wholeAtomCanonicalShape = "{atom}", strictSubstringShape = "{atom,from,to}",
            strictSubstringRule = "first-to-final issued boundary pair is invalid; whole atom must omit both boundaries",
            routeAndCarrierParity = "model, provider pin, temperature, reasoning, response_format, streaming, usage, max_tokens and provider routing body fields are unchanged",
            rows = diagnosisRows,
        });
        FreezeArtifact.AssertJson(Root, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p6l-canonical-locator-canary-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            preparedAtHead = GitHead(repo), providerCalls = 0, goldRead = false, goldMutation = "NONE",
            protocol = V5SparseCandidateRequestComposerV1.CompactDirectoryCanonicalVersion,
            route = new { gateway = "OpenRouter", model = Envelope.Model, providerPin = Envelope.Provider, temperature = 0, reasoning = Envelope.Reasoning, responseFormat = Envelope.ResponseFormat, streaming = true, usageInclude = true },
            executionGate = new { maximumProviderCalls = 4, retry = 0, repair = false, fallback = false, full31 = false, goldRead = false },
            rows = manifestRows,
        });
    }

    private static string? GitHead(string repo)
    {
        try { using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false }); var value = process!.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return process.ExitCode == 0 ? value : null; }
        catch { return null; }
    }
}
