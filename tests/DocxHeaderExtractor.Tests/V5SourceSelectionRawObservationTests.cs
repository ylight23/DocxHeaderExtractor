using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.V5;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free structural observation of the already-frozen PACK_006 raw response
/// (<c>source-selection-remediation-canary-result.v1.json</c>, commit c287f19), which
/// <see cref="SemanticClaimResponseCodecV2_1"/> correctly rejected in full - 6 REFERENCES claims carry
/// a <c>value</c> field the schema forbids on a relation - so the runtime bound zero claims from it
/// (RESPONSE_FATAL). That is a distinct, pre-existing arity rule this fix never touched; it says
/// nothing about source selection. The response's <c>sourceParts</c> shape is still readable directly
/// from the raw JSON without going through the strict codec, and that shape is exactly what the
/// source-selection remediation canary was measuring. This test keeps the two questions separate:
/// <list type="bullet">
/// <item>runtime acceptance: RESPONSE_FATAL - unchanged, no claim from this response is bound, scored
/// or fed to any runtime path by this or any other test;</item>
/// <item>raw source-selection behavior: MEASURABLE - an observation over the same bytes, computed by
/// walking <c>claims[].subject/object.sourceParts</c> directly, never through the codec/binder.</item>
/// </list>
/// This is not partial claim acceptance and must never become one.
/// </summary>
public sealed class V5SourceSelectionRawObservationTests
{
    private const string ResultPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-result.v1.json";
    private const string OutPath = "artifacts/v5-provider-cohort-31-windows/diagnosis/source-selection-remediation-canary-raw-observation.v1.json";

    private sealed record RawObservation(
        int ClaimCount, int TotalSourceParts, int AliasOnlyParts, int VerbatimTextParts,
        IReadOnlyList<IReadOnlyList<string>> MultipartEndpoints, int RelationClaimsWithValue);

    [Fact]
    public void PACK_006_raw_response_shows_measurable_source_selection_behavior_despite_runtime_rejection()
    {
        var result = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(ResultPath))).RootElement;
        var pack006 = result.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("role").GetString() == "same-atom-normalization-heavy");
        Assert.Equal("SRC-089", pack006.GetProperty("documentId").GetString());
        Assert.Equal("RESPONSE_FATAL", pack006.GetProperty("qualification").GetProperty("wireStatus").GetString());
        Assert.False(pack006.GetProperty("qualification").GetProperty("responseUsable").GetBoolean());
        Assert.Equal(0, pack006.GetProperty("qualification").GetProperty("boundCount").GetInt32());

        var raw = pack006.GetProperty("rawResponse").GetString()!;
        var observation = AnalyzeRawStructurally(raw, DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create());

        Assert.Equal(35, observation.ClaimCount);
        Assert.Equal(57, observation.TotalSourceParts);
        Assert.Equal(57, observation.AliasOnlyParts);
        Assert.Equal(0, observation.VerbatimTextParts);
        Assert.Equal(2, observation.MultipartEndpoints.Count);
        Assert.Equal(["L0522:S0", "L0523:S0"], observation.MultipartEndpoints[0]);
        Assert.Equal(["L0555:S0", "L0556:S0"], observation.MultipartEndpoints[1]);
        Assert.Equal(6, observation.RelationClaimsWithValue);

        var report = new
        {
            schemaVersion = "v5-source-selection-remediation-raw-observation-v1",
            note = "Provider-free structural observation of an already-frozen raw response whose SemanticClaimContract " +
                "validation failed (relation-has-value on 6 REFERENCES claims - an arity rule this fix never touched) " +
                "and which the runtime therefore correctly discarded in full. Observation only: no claim from this " +
                "response is or will be bound, scored, or fed to any runtime path.",
            sourceResultCommit = "c287f19",
            documentId = pack006.GetProperty("documentId").GetString(),
            packId = pack006.GetProperty("packId").GetString(),
            role = pack006.GetProperty("role").GetString(),
            rawResponseSha256 = pack006.GetProperty("rawResponseSha256").GetString(),
            runtimeAcceptance = "RESPONSE_FATAL",
            rawSourceSelectionUsage = "MEASURABLE",
            claimCount = observation.ClaimCount,
            totalSourceParts = observation.TotalSourceParts,
            aliasOnlyParts = observation.AliasOnlyParts,
            verbatimTextParts = observation.VerbatimTextParts,
            multipartEndpointCount = observation.MultipartEndpoints.Count,
            multipartEndpoints = observation.MultipartEndpoints,
            relationClaimsWithValue = observation.RelationClaimsWithValue,
            sourceSelectionRemediationCanarySummary = new
            {
                SOURCE_SELECTION_SIGNAL_OBSERVED = "3/3",
                RUNTIME_USABLE = "2/3",
                packs = new object[]
                {
                    new { pack = "SRC-089 PACK_006", aliasOnlySignal = "57/57", multipartSignal = 2, exactTextRefusals = "not counted - response fatal", runtime = "RESPONSE_FATAL" },
                    new { pack = "SRC-089 PACK_001", aliasOnlySignal = "121/121", multipartSignal = 0, exactTextRefusals = 0, runtime = "BINDING_COMPLETE" },
                    new { pack = "SRC-095 PACK_022", aliasOnlySignal = "51/51", multipartSignal = 0, exactTextRefusals = 0, runtime = "PARTIAL_BINDING" },
                },
            },
            providerCalls = 0,
            goldRead = false,
        };

        var path = TestRepository.Path(OutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    /// <summary>
    /// Walks <c>claims[].subject/object.sourceParts</c> directly from the raw JSON, deliberately
    /// bypassing <see cref="SemanticClaimResponseCodecV2_1"/> so a schema-fatal response (like this
    /// one) can still be measured for source-selection shape. Never validates arity, evidenceNeeds or
    /// state - those are exactly the codec's job, and this never repeats or relaxes them.
    /// </summary>
    private static RawObservation AnalyzeRawStructurally(string raw, DocumentTaskContract contract)
    {
        var relationNames = contract.Relations.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var claims = document.RootElement.GetProperty("claims");
        int totalParts = 0, aliasOnly = 0, verbatimText = 0, relationClaimsWithValue = 0;
        var multipart = new List<IReadOnlyList<string>>();

        foreach (var claim in claims.EnumerateArray())
        {
            var predicate = claim.TryGetProperty("predicate", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var hasValue = claim.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String;
            if (predicate is not null && relationNames.Contains(predicate) && hasValue) relationClaimsWithValue++;

            foreach (var endpointName in new[] { "subject", "object" })
            {
                if (!claim.TryGetProperty(endpointName, out var endpoint) || endpoint.ValueKind != JsonValueKind.Object) continue;
                if (!endpoint.TryGetProperty("sourceParts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                var partsArray = parts.EnumerateArray().ToArray();
                totalParts += partsArray.Length;
                if (partsArray.Length > 1)
                    multipart.Add(partsArray
                        .Select(part => part.TryGetProperty("sourceAlias", out var alias) ? alias.GetString() ?? "" : "")
                        .ToArray());
                foreach (var part in partsArray)
                {
                    if (part.TryGetProperty("verbatimText", out var vt) && vt.ValueKind == JsonValueKind.String) verbatimText++;
                    else aliasOnly++;
                }
            }
        }

        return new RawObservation(claims.GetArrayLength(), totalParts, aliasOnly, verbatimText, multipart, relationClaimsWithValue);
    }
}
