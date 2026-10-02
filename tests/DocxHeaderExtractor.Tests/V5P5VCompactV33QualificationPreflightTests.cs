using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Qualification-only preparation for V3.3. This test deliberately builds every wire body but
/// never constructs a transport client or reads Gold. Runtime promotion is explicitly out of scope.
/// </summary>
public sealed class V5P5VCompactV33QualificationPreflightTests
{
    private const string Root = "artifacts/v5-p5v-v33-qualification";
    private static readonly DocumentTaskContract Contract =
        DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
    private static readonly V5ProviderEnvelope Envelope =
        new("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300)
        { UsageInclude = true, OpenRouterResponseCacheDisabled = true };
    private sealed record DocumentSpec(string Id, string Pdf, int Packs);
    private static readonly DocumentSpec[] Documents =
    [
        new("SRC-089", SourcePdfCorpus.Src089, 7),
        new("SRC-095", SourcePdfCorpus.Src095, 24),
    ];

    [Fact]
    public void Freeze_v33_qualification_bodies_and_measure_source_realizable_volume_without_provider()
    {
        var requestRows = new List<object>();
        var volumeRows = new List<object>();
        var count = 0;
        var totalOwned = 0;
        var maxRequestBytes = 0;
        var maxCompletionTokens = 0;
        var maxResponseCap = 0;
        var sourceConstrainedBytes = new List<int>();
        var sourceConstrainedOverflows = new List<bool>();

        foreach (var document in Documents)
        {
            var path = TestRepository.Path(document.Pdf);
            var packs = V5PdfPreflightBuilder.BuildV3(path, document.Id, Contract,
                V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId, Envelope);
            var atoms = V5PdfPreflightBuilder.LoadAtoms(path);
            Assert.Equal(document.Packs, packs.Count);
            Assert.Equal(atoms.Select(atom => atom.Alias).Order(StringComparer.Ordinal),
                packs.SelectMany(pack => pack.OwnedAliases).Order(StringComparer.Ordinal));

            foreach (var (pack, index) in packs.Select((pack, index) => (pack, index)))
            {
                var request = V5CompactDecisionComposerV3_3.Compose(Contract, pack.Packet);
                var tokens = V5SemanticCompletionBudget.Compute(request.ResponseBounds.MaxDecisions,
                    request.ResponseBounds.MaxDecisions, request.Utf8Bytes,
                    V5SemanticDecisionResponseBoundsV3.ProviderCompletionCeiling);
                var body = OpenRouterQwen37JsonObjectCarrierV3.Build(request, tokens, Envelope);
                var parentOrdinal = index + 1;
                var bodyFile = $"wire-bodies/{document.Id}_parent-{parentOrdinal:D2}.json";
                FreezeArtifact.AssertText(Root, bodyFile, Encoding.UTF8.GetString(body.PayloadBytes));

                using var prompt = JsonDocument.Parse(request.Prompt);
                Assert.Equal(V5CompactDecisionComposerV3_3.Protocol, prompt.RootElement.GetProperty("protocolVersion").GetString());
                Assert.Equal(V5CompactDecisionComposerV3_3.Version, prompt.RootElement.GetProperty("composerVersion").GetString());
                Assert.Equal("json_object", JsonDocument.Parse(body.PayloadBytes).RootElement.GetProperty("response_format").GetProperty("type").GetString());

                var sourceRealizable = BuildSourceRealizableMaximum(pack);
                var sourceRealizableBytes = Encoding.UTF8.GetByteCount(sourceRealizable.ToJsonString());
                using var response = JsonDocument.Parse(sourceRealizable.ToJsonString());
                var parse = Record.Exception(() => V5CompactDecisionContractV3_3.Parse(response.RootElement, Contract,
                    pack.Packet.SubjectEvidence, pack.Packet.ContextOnlyEvidence));
                var exceedsCap = sourceRealizableBytes > request.ResponseBounds.MaxResponseUtf8Bytes;
                Assert.Equal(exceedsCap, parse is not null);
                if (parse is not null)
                    Assert.StartsWith("compact-response-byte-budget-exceeded", parse.Message, StringComparison.Ordinal);

                var ownedHash = Hash(Encoding.UTF8.GetBytes(string.Join("\n", pack.OwnedAliases)));
                requestRows.Add(new
                {
                    documentId = document.Id, packId = pack.PackId, parentOrdinal,
                    ownedAliasCount = pack.OwnedAliases.Count, visibleAliasCount = pack.VisibleAliases.Count,
                    ownedAliasHash = ownedHash, semanticRequestHash = request.RequestHash,
                    providerRequestHash = body.Hash, providerRequestBytes = body.Bytes,
                    requestUtf8Bytes = request.Utf8Bytes, maxCompletionTokens = tokens,
                    maxResponseUtf8Bytes = request.ResponseBounds.MaxResponseUtf8Bytes,
                    protocolVersion = V5CompactDecisionComposerV3_3.Protocol,
                    composerVersion = V5CompactDecisionComposerV3_3.Version, providerBodyFile = bodyFile,
                });
                volumeRows.Add(new
                {
                    documentId = document.Id, packId = pack.PackId, parentOrdinal,
                    ownedCount = pack.OwnedAliases.Count, visibleCount = pack.VisibleAliases.Count,
                    legalClaimCount = request.ResponseBounds.MaxClaimsTotal,
                    sourceRealizableUtf8Bytes = sourceRealizableBytes,
                    configuredResponseUtf8Bytes = request.ResponseBounds.MaxResponseUtf8Bytes,
                    exceedsConfiguredCap = exceedsCap,
                    parserResult = parse is null ? "PARSER_ACCEPTED" : "RESPONSE_BYTE_CAP_EXCEEDED",
                });
                sourceConstrainedBytes.Add(sourceRealizableBytes);
                sourceConstrainedOverflows.Add(exceedsCap);
                count++; totalOwned += pack.OwnedAliases.Count;
                maxRequestBytes = Math.Max(maxRequestBytes, request.Utf8Bytes);
                maxCompletionTokens = Math.Max(maxCompletionTokens, tokens);
                maxResponseCap = Math.Max(maxResponseCap, request.ResponseBounds.MaxResponseUtf8Bytes);
            }
        }

        Assert.Equal(31, count);
        Assert.Equal(2884, totalOwned);
        var maxSourceRealizable = sourceConstrainedBytes.Max();
        var allFit = sourceConstrainedOverflows.All(overflow => !overflow);
        var gate = allFit ? "OPEN_FOR_SEPARATE_CANARY_AUTHORIZATION" : "P5U_RESPONSE_VOLUME_GATE";
        FreezeArtifact.AssertJson(Root, "source-realizable-volume.v1.json", new
        {
            schemaVersion = "v5-p5v-v33-source-realizable-volume-v1",
            providerCalls = 0, goldRead = false, goldMutation = "NONE",
            method = "Per pack, construct an admissible maximum-count initial-response stress witness under the current 129/10 bounds. Every emitted selection is an actual strict substring of its request-local source atom; source handles are chosen to maximize available strict-substring bytes and no synthetic 318-byte source text or existingClaimId is used.",
            caveat = "This is a source-constrained legal stress witness, not a task-derived semantic cardinality proof. A witness above the cap conclusively keeps the gate closed; it intentionally retains the historical aggregate claim cap.",
            configuredMaxResponseUtf8Bytes = maxResponseCap,
            maximumSourceRealizableUtf8Bytes = maxSourceRealizable,
            all31FitConfiguredCap = allFit,
            gate,
            rows = volumeRows,
        });
        FreezeArtifact.AssertJson(Root, "preflight.v1.json", new
        {
            schemaVersion = "v5-p5v-v33-full31-preflight-v1", status = "PREPARED_NOT_AUTHORIZED",
            protocol = V5CompactDecisionComposerV3_3.Protocol, composer = V5CompactDecisionComposerV3_3.Version,
            packingPolicy = V5PdfPreflightBuilder.PdfResourceBoundedPackingPolicyId,
            cohort = new { parentPacks = count, ownedOccurrenceCount = totalOwned, ownedExactlyOnce = true,
                maxRequestUtf8Bytes = maxRequestBytes, maxCompletionTokens, maxResponseUtf8Bytes = maxResponseCap },
            providerCalls = 0, goldRead = false, goldMutation = "NONE", blockedBy = gate,
        });
        FreezeArtifact.AssertJson(Root, "execution-manifest.v1.json", new
        {
            schemaVersion = "v5-p5v-v33-full31-execution-manifest-v1", status = "PREPARED_NOT_AUTHORIZED",
            protocol = V5CompactDecisionComposerV3_3.Protocol, composer = V5CompactDecisionComposerV3_3.Version,
            route = new { gateway = "openrouter", api = "chat-completions", model = Envelope.Model, providerPin = Envelope.Provider,
                temperature = 0, reasoning = Envelope.Reasoning, stream = true, responseFormat = Envelope.ResponseFormat,
                fallback = false, repair = false, retry = 0 },
            providerCalls = 0, goldRead = false, goldMutation = "NONE", blockedBy = gate,
            authorization = new { providerExecutionAuthorized = false, explicitAuthorizationRequired = true,
                runnerContract = "recompose V3.3; require byte-for-byte frozen body/hash parity; execute one frozen body per attempt; Parse V3.3 then Bind V3.3; stop before network if result/checkpoint exists" },
            requests = requestRows,
        });
    }

    private static JsonObject BuildSourceRealizableMaximum(V5PackedDecisionRequestV3 pack)
    {
        Assert.Empty(pack.Packet.OpenOrConflictedClaims);
        var owned = pack.Packet.SubjectEvidence;
        var visible = owned.Concat(pack.Packet.ContextOnlyEvidence).ToArray();
        var maxClaims = Math.Min(V5SemanticDecisionResponseBoundsV3.HistoricalMaxClaimsPerResponse,
            owned.Count * V5SemanticDecisionResponseBoundsV3.HistoricalMaxClaimsPerSubject);
        var targetChoices = visible.Select((node, index) => new { Index = index, Bytes = SelectionBytes(node.Text) })
            .OrderByDescending(item => item.Bytes).ThenBy(item => item.Index).Take(6).OrderBy(item => item.Index).ToArray();
        var primaries = owned.Select((node, index) => new
            {
                Index = index,
                Bytes = SelectionBytes(node.Text) + owned.Select((following, followingIndex) => new { Index = followingIndex, Bytes = SelectionBytes(following.Text) })
                    .Where(item => item.Index > index).OrderByDescending(item => item.Bytes).Take(5).Sum(item => item.Bytes),
            })
            .OrderByDescending(item => item.Bytes).ThenBy(item => item.Index).Take((maxClaims + 9) / 10).OrderBy(item => item.Index).ToArray();
        var decisions = new JsonArray();
        var remaining = maxClaims;
        foreach (var primaryChoice in primaries)
        {
            var primary = primaryChoice.Index;
            var claims = new JsonArray();
            for (var claim = 0; claim < 10 && remaining > 0; claim++, remaining--)
            {
                var additional = new JsonArray();
                foreach (var index in owned.Select((node, index) => new { Index = index, Bytes = SelectionBytes(node.Text) })
                    .Where(item => item.Index > primary).OrderByDescending(item => item.Bytes).ThenBy(item => item.Index).Take(5).OrderBy(item => item.Index).Select(item => item.Index))
                    additional.Add(Part("ownedIndex", index, Selection(owned[index].Text)));
                var targetParts = new JsonArray();
                foreach (var target in targetChoices)
                {
                    var index = target.Index;
                    targetParts.Add(new JsonObject { ["sourceGroup"] = index < owned.Count ? "OWNED" : "CONTEXT_ONLY", ["sourceIndex"] = index < owned.Count ? index : index - owned.Count,
                        ["selection"] = Selection(visible[index].Text) });
                }
                claims.Add(new JsonObject
                {
                    ["predicate"] = "CONTINUES", ["subjectSelection"] = Selection(owned[primary].Text),
                    ["additionalSubjectParts"] = additional, ["targetParts"] = targetParts,
                    ["state"] = "RESOLVED", ["evidenceNeeds"] = new JsonArray(),
                });
            }
            decisions.Add(new JsonObject { ["ownedIndex"] = primary, ["claims"] = claims });
        }
        Assert.Equal(maxClaims, decisions.OfType<JsonObject>().Sum(decision => decision["claims"]!.AsArray().Count));
        return new JsonObject { ["decisions"] = decisions };
    }

    private static JsonObject Part(string indexName, int index, JsonObject? selection)
    {
        var result = new JsonObject { [indexName] = index };
        if (selection is not null) result["selection"] = selection;
        return result;
    }

    private static JsonObject? Selection(string source)
    {
        // The first UTF-8 bounded proper prefix is an actual strict substring. A one-rune atom has
        // no legal non-empty strict substring and correctly uses the compact whole-atom form.
        if (!source.EnumerateRunes().Skip(1).Any()) return null;
        var builder = new StringBuilder();
        foreach (var rune in source.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + rune.Utf8SequenceLength > 318) break;
            builder.Append(rune);
        }
        var value = builder.ToString();
        if (value.Length == 0 || string.Equals(value, source, StringComparison.Ordinal))
            value = source.EnumerateRunes().First().ToString();
        return new JsonObject { ["verbatimText"] = value };
    }

    private static int SelectionBytes(string source) => Selection(source) is { } selection
        ? Encoding.UTF8.GetByteCount(selection["verbatimText"]!.GetValue<string>()) : 0;

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
