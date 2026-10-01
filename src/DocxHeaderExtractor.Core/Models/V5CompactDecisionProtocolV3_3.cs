using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Core.Models;

/// <summary>
/// Experimental successor to V3.2.  It deliberately has a separate wire type: V3.2 remains the
/// historical replay contract.  A selection is a strict substring only; absence means whole atom.
/// </summary>
public static class V5CompactDecisionComposerV3_3
{
    public const string Version = "v5-semantic-decision-composer-3.3";
    public const string Protocol = "v5-source-backed-decision-3.3";

    private static readonly string Instructions = string.Join("\n", [
        "Return sparse decisions only for owned subjects with a semantic assertion.",
        "ownedIndex is the only subject handle; do not emit it more than once.",
        "A missing subjectSelection means the whole owned atom. Never copy whole-atom source text.",
        "subjectSelection, additionalSubjectParts[].selection, and targetParts[].selection are strict substrings only: when present they require verbatimText exactly as in source; occurrence is optional.",
        "No source context fields exist on this wire. Whole multipart and whole relation targets use handles only.",
        "Use additional owned indexes in increasing order. Target handles use OWNED or CONTEXT_ONLY local indexes.",
        "Use only declared predicates and relations, and return only the declared schema.",
    ]);

    public static V5ComposedSemanticDecisionRequestV3 Compose(DocumentTaskContract contract, V5SemanticDecisionRequestPacketV3 packet)
    {
        var old = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(packet.SubjectEvidence.Count, packet.SubjectEvidence.Count + packet.ContextOnlyEvidence.Count);
        var bounds = old with { MaxResponseUtf8Bytes = old.MaxResponseUtf8Bytes };
        var canonical = new
        {
            composerVersion = Version,
            protocolVersion = Protocol,
            contract,
            claimShapes = V5ClaimShapesV2_1.Generate(contract),
            responseSchema = V5CompactDecisionContractV3_3.Schema(contract, packet.SubjectEvidence.Count, packet.ContextOnlyEvidence.Count, bounds),
            responseBounds = bounds,
            sourceSelectionPolicy = CompactSourceSelectionPolicy(),
            instructions = string.Join("\n", Instructions, $"The complete response must serialize to at most {bounds.MaxResponseUtf8Bytes} UTF-8 bytes and contain at most {bounds.MaxClaimsTotal} claims."),
            packet = V5ProviderSemanticPacketV3_2.From(packet),
        };
        var prompt = JsonSerializer.Serialize(canonical, new JsonSerializerOptions(CanonicalJson.Options) { WriteIndented = false }).ReplaceLineEndings("\n");
        var schema = JsonSerializer.Serialize(canonical.responseSchema, CanonicalJson.Options);
        return new(Version, prompt, Hashing.Sha256(canonical.instructions.ReplaceLineEndings("\n")), Hashing.Sha256(schema),
            Hashing.Sha256(prompt), Encoding.UTF8.GetByteCount(prompt), bounds);
    }

    private static object CompactSourceSelectionPolicy() => new
    {
        version = "v5-compact-source-selection-policy-3.3",
        wholeAtom = new { representation = "local handle only", selection = "omitted" },
        strictSubstring = new { representation = "selection object", verbatimText = "required exact source substring", occurrence = "optional positive occurrence" },
        multipart = new { primary = "ownedIndex", additional = "ownedIndex plus optional strict substring selection", ordering = "increasing ownedIndex" },
        relationTarget = new { handle = "sourceGroup plus sourceIndex", selection = "optional strict substring only" },
    };
}

public sealed record V5CompactDecisionTextSelectionV3_3(
    [property: JsonPropertyName("verbatimText")] string VerbatimText,
    [property: JsonPropertyName("occurrence")] int? Occurrence = null);

public sealed record V5CompactAdditionalOwnedPartV3_3(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("selection")] V5CompactDecisionTextSelectionV3_3? Selection = null);

public sealed record V5CompactTargetPartV3_3(
    [property: JsonPropertyName("sourceGroup")] string SourceGroup,
    [property: JsonPropertyName("sourceIndex")] int SourceIndex,
    [property: JsonPropertyName("selection")] V5CompactDecisionTextSelectionV3_3? Selection = null);

public sealed record V5CompactDecisionClaimV3_3(
    [property: JsonPropertyName("predicate")] string Predicate,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("subjectSelection")] V5CompactDecisionTextSelectionV3_3? SubjectSelection = null,
    [property: JsonPropertyName("additionalSubjectParts")] IReadOnlyList<V5CompactAdditionalOwnedPartV3_3>? AdditionalSubjectParts = null,
    [property: JsonPropertyName("targetParts")] IReadOnlyList<V5CompactTargetPartV3_3>? TargetParts = null,
    [property: JsonPropertyName("state")] ClaimResolutionState State = ClaimResolutionState.RESOLVED,
    [property: JsonPropertyName("evidenceNeeds")] IReadOnlyList<EvidenceNeed>? EvidenceNeeds = null,
    [property: JsonPropertyName("existingClaimId")] string? ExistingClaimId = null);

public sealed record V5CompactSubjectDecisionV3_3(
    [property: JsonPropertyName("ownedIndex")] int OwnedIndex,
    [property: JsonPropertyName("claims")] IReadOnlyList<V5CompactDecisionClaimV3_3> Claims)
{ [JsonIgnore] public int WireOrdinal { get; init; } = -1; }

public sealed record V5CompactDecisionResponseV3_3(
    [property: JsonPropertyName("decisions")] IReadOnlyList<V5CompactSubjectDecisionV3_3> Decisions)
{ [JsonIgnore] public IReadOnlyDictionary<string, string> ParseRefusals { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal); }

public static class V5CompactDecisionContractV3_3
{
    public static object Schema(DocumentTaskContract contract, int ownedCount, int contextOnlyCount, V5SemanticDecisionResponseBoundsV3 bounds)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(V5SemanticSparseDecisionContractV3_1.Schema(contract, ownedCount, contextOnlyCount, bounds), CanonicalJson.Options))!;
        ReplaceSelectionSchemas(root, bounds);
        return root;
    }

    private static void ReplaceSelectionSchemas(JsonNode node, V5SemanticDecisionResponseBoundsV3 bounds)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in new[] { "subjectSelection", "selection" })
                if (obj["properties"] is JsonObject properties && properties.ContainsKey(name))
                    properties[name] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["verbatimText"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = bounds.MaxSelectionStringUtf8Bytes },
                            ["occurrence"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = int.MaxValue },
                        },
                        ["required"] = new JsonArray("verbatimText"),
                    };
            foreach (var value in obj.ToArray().Select(pair => pair.Value).Where(value => value is not null)) ReplaceSelectionSchemas(value!, bounds);
        }
        else if (node is JsonArray array) foreach (var item in array.Where(item => item is not null)) ReplaceSelectionSchemas(item!, bounds);
    }

    public static V5CompactDecisionResponseV3_3 Parse(JsonElement payload, DocumentTaskContract contract, int ownedCount, int contextOnlyCount)
    {
        var bounds = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount) with { MaxResponseUtf8Bytes = V5SemanticDecisionResponseBoundsV3.ForEvidenceCounts(ownedCount, ownedCount + contextOnlyCount).MaxResponseUtf8Bytes };
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > bounds.MaxResponseUtf8Bytes) throw new InvalidOperationException($"compact-response-byte-budget-exceeded:max={bounds.MaxResponseUtf8Bytes}");
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => !p.NameEquals("decisions")) || !payload.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() > ownedCount) throw new InvalidOperationException("compact-response-shape-invalid");
        var options = new JsonSerializerOptions(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
        var parsed = new List<V5CompactSubjectDecisionV3_3>(); var refusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (decision, index) in decisions.EnumerateArray().Select((item, i) => (item, i)))
        {
            var key = $"compact-decision-{index}";
            try
            {
                if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Any(p => !p.NameEquals("ownedIndex") && !p.NameEquals("claims")) || !decision.TryGetProperty("ownedIndex", out _) || !decision.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("missing-or-extra-decision-field");
                var typed = decision.Deserialize<V5CompactSubjectDecisionV3_3>(options) ?? throw new InvalidOperationException("empty-decision");
                ValidateSelections(typed, bounds); parsed.Add(typed with { WireOrdinal = index });
            }
            catch (JsonException) { refusals[key] = "json-decision-schema-invalid"; }
            catch (InvalidOperationException ex) { refusals[key] = ex.Message; }
        }
        return new(parsed) { ParseRefusals = refusals };
    }

    /// <summary>
    /// Provider-facing V3.3 parsing with the request-local source universe.  Unlike V3.2, a
    /// selection which names an entire atom is rejected before binding: whole atoms have exactly
    /// one legal representation on this wire, namely their local handle with no selection.
    /// </summary>
    public static V5CompactDecisionResponseV3_3 Parse(JsonElement payload, DocumentTaskContract contract,
        IReadOnlyList<EvidenceNode> owned, IReadOnlyList<EvidenceNode> contextOnly)
    {
        var parsed = Parse(payload, contract, owned.Count, contextOnly.Count);
        var accepted = new List<V5CompactSubjectDecisionV3_3>();
        var refusals = new Dictionary<string, string>(parsed.ParseRefusals, StringComparer.Ordinal);
        foreach (var decision in parsed.Decisions)
        {
            var key = $"compact-decision-{decision.WireOrdinal}";
            try
            {
                ValidateStrictSubstrings(decision, owned, contextOnly);
                accepted.Add(decision);
            }
            catch (InvalidOperationException ex) { refusals[key] = ex.Message; }
        }
        return new(accepted) { ParseRefusals = refusals };
    }

    public static V5DecisionBindingResultV3 Bind(string requestId, V5CompactDecisionResponseV3_3 response, DocumentTaskContract contract,
        IReadOnlyList<EvidenceNode> owned, IReadOnlyList<EvidenceNode> context, IReadOnlyList<SemanticSourceAtom> atoms, ClaimBindingScope scope)
    {
        var converted = new V5SemanticSparseDecisionResponseV3_1(response.Decisions.Select(decision => new V5SemanticSparseSubjectDecisionV3_1(decision.OwnedIndex,
            decision.Claims.Select(ToV31).ToArray()) { WireOrdinal = decision.WireOrdinal }).ToArray()) { ParseRefusals = response.ParseRefusals };
        return V5SemanticSparseDecisionContractV3_1.Bind(requestId, converted, contract, owned, context, atoms, scope);
    }

    private static V5SemanticDecisionClaimV3 ToV31(V5CompactDecisionClaimV3_3 claim) => new(claim.Predicate, claim.Value,
        ToV31(claim.SubjectSelection), claim.AdditionalSubjectParts?.Select(part => new V5AdditionalOwnedSubjectPartV3(part.OwnedIndex, ToV31(part.Selection))).ToArray(),
        claim.TargetParts?.Select(part => new V5VisibleTargetPartV3(part.SourceGroup, part.SourceIndex, ToV31(part.Selection))).ToArray(), claim.State, claim.EvidenceNeeds, claim.ExistingClaimId);
    private static V5DecisionTextSelectionV3? ToV31(V5CompactDecisionTextSelectionV3_3? selection) => selection is null ? null : new(selection.VerbatimText, selection.Occurrence);
    private static void ValidateSelections(V5CompactSubjectDecisionV3_3 decision, V5SemanticDecisionResponseBoundsV3 bounds)
    {
        foreach (var selection in decision.Claims.SelectMany(claim => new[] { claim.SubjectSelection }.Concat((claim.AdditionalSubjectParts ?? []).Select(part => part.Selection)).Concat((claim.TargetParts ?? []).Select(part => part.Selection))).Where(selection => selection is not null))
        {
            if (string.IsNullOrWhiteSpace(selection!.VerbatimText) || Encoding.UTF8.GetByteCount(selection.VerbatimText) > bounds.MaxSelectionStringUtf8Bytes || selection.Occurrence is < 1)
                throw new InvalidOperationException("compact-selection-invalid");
        }
    }

    private static void ValidateStrictSubstrings(V5CompactSubjectDecisionV3_3 decision,
        IReadOnlyList<EvidenceNode> owned, IReadOnlyList<EvidenceNode> contextOnly)
    {
        ValidateSelection(decision.Claims.Select(claim => (claim.SubjectSelection, decision.OwnedIndex)), owned);
        foreach (var part in decision.Claims.SelectMany(claim => claim.AdditionalSubjectParts ?? []))
            ValidateSelection([(part.Selection, part.OwnedIndex)], owned);
        foreach (var part in decision.Claims.SelectMany(claim => claim.TargetParts ?? []))
        {
            var source = string.Equals(part.SourceGroup, "CONTEXT_ONLY", StringComparison.Ordinal) ? contextOnly : owned;
            ValidateSelection([(part.Selection, part.SourceIndex)], source);
        }
    }

    private static void ValidateSelection(IEnumerable<(V5CompactDecisionTextSelectionV3_3? Selection, int Index)> selections,
        IReadOnlyList<EvidenceNode> source)
    {
        foreach (var (selection, index) in selections)
        {
            if (selection is null || index < 0 || index >= source.Count) continue;
            var text = source[index].Text;
            if (string.Equals(selection.VerbatimText, text, StringComparison.Ordinal))
                throw new InvalidOperationException("whole-atom-selection-retyped");
            if (!ContainsAtOccurrence(text, selection.VerbatimText, selection.Occurrence))
                throw new InvalidOperationException("compact-selection-not-exact-source-substring");
        }
    }

    private static bool ContainsAtOccurrence(string source, string value, int? occurrence)
    {
        var desired = occurrence ?? 1;
        var from = 0;
        for (var found = 0; ; found++)
        {
            var position = source.IndexOf(value, from, StringComparison.Ordinal);
            if (position < 0) return false;
            if (found + 1 == desired) return true;
            from = position + value.Length;
        }
    }
}
