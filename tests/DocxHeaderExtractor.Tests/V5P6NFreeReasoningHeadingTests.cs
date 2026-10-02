using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class V5P6NFreeReasoningHeadingTests
{
    private const string Root = "artifacts/v5-p6n-free-reasoning-heading-ceiling";
    private const string FreeSchemaRoot = "artifacts/v5-p6n-unconstrained-output-ceiling";
    private const string BoundLocatorRoot = "artifacts/v5-p6nb-free-semantic-bound-locator";
    private const string P6LRoot = "artifacts/v5-p6l-canonical-locator-contract";
    private const string P6IRoot = "artifacts/v5-p6i-compact-locator-directory";
    private const int ResponseCap = 49_152;
    private static readonly DocumentTaskContract Contract = DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope = new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
    { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private static readonly (string Role, string DocumentId, string Pdf, int ParentOrdinal)[] Roles =
    [
        ("MAX_REQUEST_BODY", "SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", 4),
        ("L1472_OWNER_OMISSION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 17),
        ("L1710_RETYPING", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 20),
        ("MULTIPART_RELATION", "SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", 11),
    ];

    [Fact]
    public void Freeze_P6N_free_reasoning_four_pack_preflight_and_locator_parser_regressions()
    {
        var repo = TestRepository.Root();
        var p6l = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6LRoot, "execution-manifest.v1.json")))!.AsObject();
        var p6i = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, P6IRoot, "audit.v1.json")))!.AsObject();
        var p6n = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, Root, "execution-manifest.v1.json")))!.AsObject();
        Assert.Equal("PREPARED_NOT_AUTHORIZED", p6n["status"]!.GetValue<string>());
        Assert.Equal(0, p6n["providerCalls"]!.GetValue<int>());
        Assert.False(p6n["goldRead"]!.GetValue<bool>());
        Assert.Equal(4, p6n["executionGate"]!["maximumProviderCalls"]!.GetValue<int>());
        Assert.Equal(0, p6n["executionGate"]!["retry"]!.GetValue<int>());
        Assert.False(p6n["executionGate"]!["repair"]!.GetValue<bool>());
        Assert.False(p6n["executionGate"]!["fallback"]!.GetValue<bool>());
        Assert.False(p6n["executionGate"]!["postFilter"]!.GetValue<bool>());
        Assert.False(p6n["executionGate"]!["full31"]!.GetValue<bool>());
        Assert.True(p6l["providerCalls"]!.GetValue<int>() == 0 && p6i["providerCalls"]!.GetValue<int>() == 0);

        var packCache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        var rows = new List<object>();
        foreach (var role in Roles)
        {
            if (!packCache.TryGetValue(role.DocumentId, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (V5PdfPreflightBuilder.BuildV3(pdf, role.DocumentId, Contract,
                        V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                packCache.Add(role.DocumentId, source);
            }
            var pack = source.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.ParentOrdinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var p6mRequest = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var request = V5FreeHeadingCandidateProtocolV1.Compose(p6mRequest);
            using var p6mJson = JsonDocument.Parse(p6mRequest.UserMessage);
            using var p6nJson = JsonDocument.Parse(request.UserMessage);
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("ownedSubjects"), p6nJson.RootElement.GetProperty("ownedSubjects")));
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("contextOnlyEvidence"), p6nJson.RootElement.GetProperty("contextOnlyEvidence")));
            Assert.False(p6nJson.RootElement.TryGetProperty("task", out _));
            Assert.False(p6nJson.RootElement.TryGetProperty("responseContract", out _));
            Assert.False(p6nJson.RootElement.TryGetProperty("semanticFunctions", out _));
            Assert.DoesNotContain("DOCUMENT_IDENTITY", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("STRUCTURAL_REGION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("NAVIGATION_REPRESENTATION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("TABLE OF CONTENTS", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("font", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);

            var body = V5FreeHeadingCandidateProtocolV1.BuildProviderBody(request, pack.MaxCompletionTokens);
            using var bodyJson = JsonDocument.Parse(body.PayloadBytes);
            Assert.Equal("qwen/qwen3.7-flash", bodyJson.RootElement.GetProperty("model").GetString());
            Assert.Equal("alibaba", bodyJson.RootElement.GetProperty("provider").GetProperty("order")[0].GetString());
            Assert.True(bodyJson.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.False(bodyJson.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
            Assert.Equal(pack.MaxCompletionTokens, bodyJson.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(ResponseCap, p6n["maxResponseUtf8Bytes"]!.GetValue<int>());

            var frozen = p6n["rows"]!.AsArray().Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            Assert.Equal(registry.Fingerprint, frozen["registryFingerprint"]!.GetValue<string>());
            Assert.Equal(request.UserMessageSha256, frozen["semanticRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Hash, frozen["providerRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Bytes, frozen["providerRequestBytes"]!.GetValue<int>());
            Assert.Equal(pack.MaxCompletionTokens, frozen["maxCompletionTokens"]!.GetValue<int>());
            var sourceEvidenceJson = JsonSerializer.Serialize(new
            {
                ownedSubjects = p6nJson.RootElement.GetProperty("ownedSubjects"),
                contextOnlyEvidence = p6nJson.RootElement.GetProperty("contextOnlyEvidence"),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Assert.Equal(Hashing.Sha256(sourceEvidenceJson), frozen["sourceEvidenceHash"]!.GetValue<string>());
            rows.Add(new
            {
                role = role.Role, documentId = role.DocumentId, parentOrdinal = role.ParentOrdinal,
                packId = pack.PackId, ownedAtoms = pack.OwnedAliases.Count,
                sourceEvidenceHash = frozen["sourceEvidenceHash"]!.GetValue<string>(),
                registryFingerprint = registry.Fingerprint, requestHash = request.UserMessageSha256,
                providerBodyHash = body.Hash, providerBodyBytes = body.Bytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
            });
        }

        Assert.Equal(4, rows.Count);
        Assert.True(p6n["route"]!["reasoning"]!["enabled"]!.GetValue<bool>());
        Assert.False(p6n["route"]!["reasoning"]!.AsObject().ContainsKey("effort"));
        Assert.Equal("P6L canonical locator contract; response parser/binder reuse through qualification-only adapter", p6n["protocolNotes"]!.GetValue<string>());
        FreezeArtifact.AssertJson(Root, "audit.v1.json", new
        {
            schemaVersion = "v5-p6n-four-pack-free-reasoning-preflight-audit-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            sourceEvidenceParity = "P6M ownedSubjects and contextOnlyEvidence are byte-structurally equal for all four selected packs",
            ontologyInSemanticPrompt = false, semanticFunctionsInResponse = false,
            reasoningBody = new { enabled = true, effort = "OMITTED" }, maxResponseUtf8Bytes = ResponseCap,
            fixture = new { wholeAtom = "PASS", strictSubstring = "PASS", multipart = "PASS", invalidSiblingIsolation = "PASS", unknownHandleQuarantined = "PASS", malformedRootResponseFatal = "PASS" },
            rows,
        });

        AssertParserFixture();
    }

    [Fact]
    public void Freeze_P6N_unconstrained_output_arm_with_no_response_schema_or_parser_gate()
    {
        var repo = TestRepository.Root();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, FreeSchemaRoot, "execution-manifest.v1.json")))!.AsObject();
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest["status"]!.GetValue<string>());
        Assert.Equal(0, manifest["providerCalls"]!.GetValue<int>());
        Assert.False(manifest["goldRead"]!.GetValue<bool>());
        Assert.Equal(4, manifest["executionGate"]!["maximumProviderCalls"]!.GetValue<int>());
        Assert.Equal(0, manifest["executionGate"]!["retry"]!.GetValue<int>());
        Assert.False(manifest["executionGate"]!["parsing"]!.GetValue<bool>());
        Assert.False(manifest["executionGate"]!["binding"]!.GetValue<bool>());
        Assert.Equal("OMITTED", manifest["route"]!["responseFormat"]!.GetValue<string>());
        Assert.True(manifest["route"]!["reasoning"]!["enabled"]!.GetValue<bool>());
        Assert.False(manifest["route"]!["reasoning"]!.AsObject().ContainsKey("effort"));
        Assert.Equal("NONE; model may choose any response format/schema", manifest["outputSchema"]!.GetValue<string>());

        var cache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        var auditRows = new List<object>();
        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.DocumentId, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (V5PdfPreflightBuilder.BuildV3(pdf, role.DocumentId, Contract,
                        V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(role.DocumentId, source);
            }
            var pack = source.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.ParentOrdinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var request = V5FreeHeadingCandidateProtocolV1.ComposeUnschematized(canonical);
            using var p6mJson = JsonDocument.Parse(canonical.UserMessage);
            using var requestJson = JsonDocument.Parse(request.UserMessage);
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("ownedSubjects"), requestJson.RootElement.GetProperty("ownedSubjects")));
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("contextOnlyEvidence"), requestJson.RootElement.GetProperty("contextOnlyEvidence")));
            Assert.Equal(2, requestJson.RootElement.EnumerateObject().Count());
            Assert.DoesNotContain("json", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DOCUMENT_IDENTITY", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("STRUCTURAL_REGION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("NAVIGATION_REPRESENTATION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("font", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);

            var body = V5FreeHeadingCandidateProtocolV1.BuildUnconstrainedProviderBody(request, pack.MaxCompletionTokens);
            using var bodyJson = JsonDocument.Parse(body.PayloadBytes);
            Assert.False(bodyJson.RootElement.TryGetProperty("response_format", out _));
            Assert.True(bodyJson.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.False(bodyJson.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
            var baseline = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(canonical.SystemPrompt, canonical.UserMessage,
                pack.MaxCompletionTokens, Envelope);
            using var baselineJson = JsonDocument.Parse(baseline.PayloadBytes);
            foreach (var field in new[] { "model", "temperature", "max_tokens", "provider", "stream", "usage" })
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bodyJson.RootElement.GetProperty(field).GetRawText()), JsonNode.Parse(baselineJson.RootElement.GetProperty(field).GetRawText())), $"unchanged carrier field {field}");

            var row = manifest["rows"]!.AsArray().Single(item => item!["role"]!.GetValue<string>() == role.Role)!;
            var sourceEvidenceJson = JsonSerializer.Serialize(new
            {
                ownedSubjects = requestJson.RootElement.GetProperty("ownedSubjects"),
                contextOnlyEvidence = requestJson.RootElement.GetProperty("contextOnlyEvidence"),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Assert.Equal(Hashing.Sha256(sourceEvidenceJson), row["sourceEvidenceHash"]!.GetValue<string>());
            Assert.Equal(registry.Fingerprint, row["registryFingerprint"]!.GetValue<string>());
            Assert.Equal(request.UserMessageSha256, row["semanticRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Hash, row["providerRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Bytes, row["providerRequestBytes"]!.GetValue<int>());
            auditRows.Add(new
            {
                role = role.Role, documentId = role.DocumentId, parentOrdinal = role.ParentOrdinal,
                packId = pack.PackId, ownedAtoms = pack.OwnedAliases.Count,
                semanticRequestHash = request.UserMessageSha256, sourceEvidenceHash = row["sourceEvidenceHash"]!.GetValue<string>(),
                registryFingerprint = registry.Fingerprint, providerRequestHash = body.Hash, providerRequestBytes = body.Bytes,
                maxCompletionTokens = pack.MaxCompletionTokens,
            });
        }

        FreezeArtifact.AssertJson(FreeSchemaRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p6n-unconstrained-output-provider-free-audit-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            sourceEvidenceParity = "P6M ownedSubjects and contextOnlyEvidence unchanged on all four selected packs",
            semanticOntologySupplied = false, outputSchemaSupplied = false, responseFormatSent = false,
            reasoning = new { enabled = true, effort = "OMITTED" },
            parser = "NOT_RUN_BY_DESIGN", binder = "NOT_RUN_BY_DESIGN", semanticScore = "NOT_RUN",
            responseCap = ResponseCap, responseCapIsObservedAfterStream = true,
            rows = auditRows,
        });
    }

    [Fact]
    public void Freeze_P6NB_free_semantic_bound_locator_four_pack_preflight()
    {
        var repo = TestRepository.Root();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, BoundLocatorRoot, "execution-manifest.v1.json")))!.AsObject();
        Assert.Equal("PREPARED_NOT_AUTHORIZED", manifest["status"]!.GetValue<string>());
        Assert.Equal(0, manifest["providerCalls"]!.GetValue<int>());
        Assert.False(manifest["goldRead"]!.GetValue<bool>());
        Assert.Equal(4, manifest["executionGate"]!["maximumProviderCalls"]!.GetValue<int>());
        Assert.Equal(0, manifest["executionGate"]!["retry"]!.GetValue<int>());
        Assert.False(manifest["executionGate"]!["full31"]!.GetValue<bool>());
        Assert.Equal("json_object", manifest["route"]!["responseFormat"]!.GetValue<string>());
        Assert.True(manifest["route"]!["reasoning"]!["enabled"]!.GetValue<bool>());
        Assert.False(manifest["route"]!["reasoning"]!.AsObject().ContainsKey("effort"));
        Assert.Equal("v5-free-reasoning-heading-membership-source-parts-locator-1", manifest["protocol"]!.GetValue<string>());
        Assert.Contains("sourceParts[]", manifest["outputShape"]!["heading"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("P6M vs P6N-B changes ontology and reasoning together; result estimates only their combined arm difference.", manifest["causalLimit"]!.GetValue<string>());

        var cache = new Dictionary<string, (IReadOnlyList<V5PackedDecisionRequestV3> Packs, Dictionary<string, SemanticSourceAtom> Atoms)>();
        var auditRows = new List<object>();
        foreach (var role in Roles)
        {
            if (!cache.TryGetValue(role.DocumentId, out var source))
            {
                var pdf = Path.Combine(repo, role.Pdf.Replace('/', Path.DirectorySeparatorChar));
                source = (V5PdfPreflightBuilder.BuildV3(pdf, role.DocumentId, Contract,
                        V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope),
                    V5PdfPreflightBuilder.LoadAtoms(pdf).ToDictionary(atom => atom.Alias, StringComparer.Ordinal));
                cache.Add(role.DocumentId, source);
            }
            var pack = source.Packs.Single(item => item.PackId == $"{V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId}:PACK_{role.ParentOrdinal:000}");
            var registry = RequestLocalLocatorRegistry.Create(pack.OwnedAliases.Select(alias => source.Atoms[alias]).ToArray());
            var canonical = V5SparseCandidateRequestComposerV1.ComposeCompactDirectoryCanonical(Contract, pack.Packet, registry);
            var request = V5FreeHeadingCandidateProtocolV1.ComposeBoundLocator(canonical);
            using var p6mJson = JsonDocument.Parse(canonical.UserMessage);
            using var requestJson = JsonDocument.Parse(request.UserMessage);
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("ownedSubjects"), requestJson.RootElement.GetProperty("ownedSubjects")));
            Assert.True(JsonElement.DeepEquals(p6mJson.RootElement.GetProperty("contextOnlyEvidence"), requestJson.RootElement.GetProperty("contextOnlyEvidence")));
            Assert.Equal(2, requestJson.RootElement.EnumerateObject().Count());
            Assert.DoesNotContain("DOCUMENT_IDENTITY", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("STRUCTURAL_REGION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("NAVIGATION_REPRESENTATION", request.SystemPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("CAPTION", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TABLE OF CONTENTS", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("font", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
            using var promptShape = JsonDocument.Parse("{\"headings\":[{\"sourceParts\":[{\"atom\":\"A17\"}]}]}");
            var headingShape = promptShape.RootElement.GetProperty("headings")[0];
            Assert.Equal(new[] { "sourceParts" }, headingShape.EnumerateObject().Select(property => property.Name));
            Assert.Equal(new[] { "atom" }, headingShape.GetProperty("sourceParts")[0].EnumerateObject().Select(property => property.Name));

            var body = V5FreeHeadingCandidateProtocolV1.BuildBoundLocatorProviderBody(request, pack.MaxCompletionTokens);
            using var bodyJson = JsonDocument.Parse(body.PayloadBytes);
            Assert.Equal("json_object", bodyJson.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            Assert.True(bodyJson.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
            Assert.False(bodyJson.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
            Assert.Equal(pack.MaxCompletionTokens, bodyJson.RootElement.GetProperty("max_tokens").GetInt32());
            var baseline = OpenRouterQwen37JsonObjectCarrierV2_1.BuildFromRaw(canonical.SystemPrompt, canonical.UserMessage,
                pack.MaxCompletionTokens, Envelope);
            using var baselineJson = JsonDocument.Parse(baseline.PayloadBytes);
            foreach (var field in new[] { "model", "temperature", "max_tokens", "response_format", "provider", "stream", "usage" })
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bodyJson.RootElement.GetProperty(field).GetRawText()), JsonNode.Parse(baselineJson.RootElement.GetProperty(field).GetRawText())), $"unchanged carrier field {field}");

            var frozen = manifest["rows"]!.AsArray().Single(row => row!["role"]!.GetValue<string>() == role.Role)!;
            var sourceEvidenceJson = JsonSerializer.Serialize(new
            {
                ownedSubjects = requestJson.RootElement.GetProperty("ownedSubjects"),
                contextOnlyEvidence = requestJson.RootElement.GetProperty("contextOnlyEvidence"),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Assert.Equal(Hashing.Sha256(sourceEvidenceJson), frozen["sourceEvidenceHash"]!.GetValue<string>());
            Assert.Equal(registry.Fingerprint, frozen["registryFingerprint"]!.GetValue<string>());
            Assert.Equal(request.UserMessageSha256, frozen["semanticRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Hash, frozen["providerRequestHash"]!.GetValue<string>());
            Assert.Equal(body.Bytes, frozen["providerRequestBytes"]!.GetValue<int>());
            auditRows.Add(new
            {
                role = role.Role, documentId = role.DocumentId, parentOrdinal = role.ParentOrdinal, packId = pack.PackId,
                ownedAtoms = pack.OwnedAliases.Count, semanticRequestHash = request.UserMessageSha256,
                sourceEvidenceHash = frozen["sourceEvidenceHash"]!.GetValue<string>(), registryFingerprint = registry.Fingerprint,
                providerRequestHash = body.Hash, providerRequestBytes = body.Bytes, maxCompletionTokens = pack.MaxCompletionTokens,
            });
        }

        FreezeArtifact.AssertJson(BoundLocatorRoot, "audit.v1.json", new
        {
            schemaVersion = "v5-p6nb-bound-locator-provider-free-audit-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE", sharedRuntime = "UNCHANGED",
            sourceEvidenceParity = "P6M ownedSubjects/contextOnlyEvidence byte-structurally equal for all four selected packs",
            ontologySupplied = false, semanticFunctionsInOutput = false, locatorContract = "canonical P6L sourceParts[]",
            reasoning = new { enabled = true, effort = "OMITTED" }, responseFormat = "json_object",
            parser = "sourceParts-to-P6L adapter then production-qualified occurrence parser", binder = "RequestLocalLocatorRegistry.Decode / SemanticSourcePartBinder",
            causalLimit = "P6M comparison changes ontology and reasoning together; no individual causal attribution",
            fixture = new { wholeAtom = "PASS", strictSubstring = "PASS", multipart = "PASS", invalidSiblingIsolation = "PASS", unknownHandleQuarantined = "PASS", malformedRootResponseFatal = "PASS" },
            rows = auditRows,
        });
        AssertSourcePartsParserFixture();
    }

    private static void AssertParserFixture()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0001:S0", "fixture", 1, 1, 0, 0, "Whole atom"),
            new SemanticSourceAtom("L0002:S0", "fixture", 2, 1, 1, 0, "prefix StrictSuffix"),
            new SemanticSourceAtom("L0003:S0", "fixture", 3, 1, 2, 0, "Multipart first"),
            new SemanticSourceAtom("L0004:S0", "fixture", 4, 1, 3, 0, "Multipart second"),
        };
        var registry = RequestLocalLocatorRegistry.Create(atoms);
        var strictFrom = registry.BoundaryHandle(1, 7);
        var strictTo = registry.BoundaryHandle(1, 13);
        var validJson = new JsonObject
        {
            ["headings"] = new JsonArray
            {
                new JsonObject { ["locator"] = new JsonObject { ["primary"] = new JsonObject { ["atom"] = "A0" }, ["additionalParts"] = new JsonArray() } },
                new JsonObject { ["locator"] = new JsonObject { ["primary"] = new JsonObject { ["atom"] = "A1", ["from"] = strictFrom, ["to"] = strictTo }, ["additionalParts"] = new JsonArray() } },
                new JsonObject { ["locator"] = new JsonObject { ["primary"] = new JsonObject { ["atom"] = "A2" }, ["additionalParts"] = new JsonArray(new JsonObject { ["atom"] = "A3" }) } },
            },
        };
        using var valid = JsonDocument.Parse(validJson.ToJsonString());
        var parsed = V5FreeHeadingCandidateProtocolV1.ParseAndBind(valid.RootElement, System.Text.Encoding.UTF8.GetByteCount(valid.RootElement.GetRawText()), ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 });
        Assert.Empty(parsed.Quarantined);
        Assert.Equal(3, parsed.Response.Occurrences.Count);
        Assert.Contains(parsed.Response.Occurrences, occurrence => occurrence.Primary.From == strictFrom);
        Assert.Contains(parsed.Response.Occurrences, occurrence => occurrence.AdditionalParts.Count == 1);

        using var sibling = JsonDocument.Parse("""{"headings":[{"locator":{"primary":{"atom":"A0"},"additionalParts":[]}},{"locator":{"primary":{"atom":"A99"},"additionalParts":[]}},{"locator":{"primary":{"atom":"A2"},"additionalParts":[]}}]}""");
        var isolated = V5FreeHeadingCandidateProtocolV1.ParseAndBind(sibling.RootElement, System.Text.Encoding.UTF8.GetByteCount(sibling.RootElement.GetRawText()), ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 });
        Assert.Equal(2, isolated.Response.Occurrences.Count);
        Assert.Single(isolated.Quarantined);

        using var badRoot = JsonDocument.Parse("""{"headings":{},"extra":true}""");
        Assert.Throws<InvalidOperationException>(() => V5FreeHeadingCandidateProtocolV1.ParseAndBind(badRoot.RootElement, 28, ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 }));
    }

    private static void AssertSourcePartsParserFixture()
    {
        var atoms = new[]
        {
            new SemanticSourceAtom("L0010:S0", "fixture", 10, 1, 0, 0, "Whole atom"),
            new SemanticSourceAtom("L0011:S0", "fixture", 11, 1, 1, 0, "prefix StrictSuffix"),
            new SemanticSourceAtom("L0012:S0", "fixture", 12, 1, 2, 0, "Multipart first"),
            new SemanticSourceAtom("L0013:S0", "fixture", 13, 1, 3, 0, "Multipart second"),
        };
        var registry = RequestLocalLocatorRegistry.Create(atoms);
        var from = registry.BoundaryHandle(1, 7);
        var to = registry.BoundaryHandle(1, 13);
        var fixture = new JsonObject
        {
            ["headings"] = new JsonArray
            {
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A0" }) },
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A1", ["from"] = from, ["to"] = to }) },
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A2" }, new JsonObject { ["atom"] = "A3" }) },
            },
        };
        using var valid = JsonDocument.Parse(fixture.ToJsonString());
        var validResult = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(valid.RootElement,
            Encoding.UTF8.GetByteCount(valid.RootElement.GetRawText()), ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 });
        Assert.Empty(validResult.Quarantined);
        Assert.Equal(3, validResult.Response.Occurrences.Count);
        Assert.Contains(validResult.Response.Occurrences, item => item.Primary.From == from);
        Assert.Contains(validResult.Response.Occurrences, item => item.AdditionalParts.Count == 1);

        var siblingFixture = new JsonObject
        {
            ["headings"] = new JsonArray
            {
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A0" }) },
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A999" }) },
                new JsonObject { ["sourceParts"] = new JsonArray(new JsonObject { ["atom"] = "A2" }) },
            },
        };
        using var sibling = JsonDocument.Parse(siblingFixture.ToJsonString());
        var isolated = V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(sibling.RootElement,
            Encoding.UTF8.GetByteCount(sibling.RootElement.GetRawText()), ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 });
        Assert.Equal(2, isolated.Response.Occurrences.Count);
        Assert.Single(isolated.Quarantined);

        using var malformed = JsonDocument.Parse("""{"headings":[],"other":true}""");
        Assert.Throws<InvalidOperationException>(() => V5FreeHeadingCandidateProtocolV1.ParseAndBindSourceParts(malformed.RootElement, 30, ResponseCap, registry, new HashSet<int> { 0, 1, 2, 3 }));
    }
}
