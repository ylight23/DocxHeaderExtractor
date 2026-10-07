using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free forensic decomposition of the frozen mixed-prompt H2C overextent residuals.</summary>
public sealed class V5P6TH2CMixedPromptResidualAuditTests
{
    private const string Root = "artifacts/v5-p6t-function-membership";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string V1ManifestPath = Root + "/p6th2c-end-pointer-preflight/h2c-exact-end-pointer-preflight.v1.json";
    private const string V2ManifestPath = Root + "/p6th2c-end-pointer-preflight-v2/h2c-exact-end-pointer-preflight.v2.json";
    private const string PrimaryRoot = Root + "/p6th2c-end-pointer-capture-20261005";
    private const string ClarifiedRoot = Root + "/p6th2c-end-pointer-clarified-retry-20261005";
    private const string G2ARoot = Root + "/p6tg2a-full-pack-population-canary-20261005";
    private const string OutputRoot = Root + "/p6th2c-mixed-prompt-residual-audit";

    private static readonly Source[] Sources =
    [
        new("SRC-089", "todo10_8/heading_corpus_100/06_dich_song_ngu/089_ND_195-2013_Luat_Xuat_ban_EN.pdf", "p6tf1-preflight/retry-src089-result.v1.json", F1Shape.ResultRow, true),
        new("SRC-041", "todo10_8/heading_corpus_100/03_tai_chinh_ke_toan/041_IBRD_Financial_Statements_June_2025.pdf", "p6te-src041-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("SRC-095", "todo10_8/heading_corpus_100/07_system_generated/095_RFC9114_HTTP_3.pdf", "p6tf1-preflight/result.v1.json", F1Shape.ResultRows, true),
        new("DOC-0252", "todo10_8/heading_corpus_100/05_bien_ban_hop/072_ICP_TAG_Minutes_Mar_2025.pdf", "p6te-doc0252-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
        new("DOC-0256", "todo10_8/heading_corpus_100/05_bien_ban_hop/076_ICP_IACG08_Minutes_2023.pdf", "p6te-doc0256-e-challenge/f1.raw-capture.v1.json", F1Shape.RawCapture, false),
    ];

    [Fact]
    public void Frozen_mixed_H2C_overextents_are_classified_without_provider_or_Gold_mutation()
    {
        using var v1 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V1ManifestPath)));
        using var v2 = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(V2ManifestPath)));
        var primaryRequests = v1.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        var v2Requests = v2.RootElement.GetProperty("requestUniverse").EnumerateArray().ToDictionary(Key, StringComparer.Ordinal);
        Assert.Equal(31, primaryRequests.Count);
        Assert.Equal(31, v2Requests.Count);

        var authority = Sources.ToDictionary(source => source.DocumentId, BuildAuthority, StringComparer.Ordinal);
        var clarifiedFiles = Directory.GetFiles(TestRepository.Path(ClarifiedRoot), "*.raw-capture.v2.json", SearchOption.AllDirectories);
        Assert.Equal(2, clarifiedFiles.Length);
        var rows = new List<Residual>();

        foreach (var request in v2Requests.Values)
        {
            var documentId = request.GetProperty("DocumentId").GetString()!;
            var packId = request.GetProperty("PackId").GetString()!;
            var anchor = request.GetProperty("Anchor").GetString()!;
            var key = Key(request);
            var issued = request.GetProperty("IssuedOccurrences").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var selected = Select(documentId, anchor, issued, clarifiedFiles);
            var expectedRequest = selected.PromptVersion == "PRIMARY_PROMPT_V1" ? primaryRequests[key] : request;
            Assert.Equal(expectedRequest.GetProperty("ProviderBodySha256").GetString(), selected.ProviderBodySha256);

            var doc = authority[documentId];
            var predictedAliases = selected.Members.Select(value => doc.AliasByOccurrence[value]).ToArray();
            var anchorAlias = doc.AliasByOccurrence[anchor];
            var gold = doc.GoldExtents.SingleOrDefault(value => value[0] == anchorAlias);
            if (gold is null || !IsOverextent(predictedAliases, gold)) continue;

            var goldFirstIndex = gold.Length;
            Assert.True(goldFirstIndex < issued.Length, $"Gold first-outside is unavailable for {documentId}/{anchor}");
            var goldOutside = issued[goldFirstIndex];
            var predictedFirstIndex = selected.Members.Count;
            var predictedOutside = predictedFirstIndex < issued.Length ? issued[predictedFirstIndex] : null;
            var goldOutsideAlias = doc.AliasByOccurrence[goldOutside];
            var boundaryKind = BoundaryFamily(doc, goldOutside, selected.FirstOutsideRole);
            var endFacts = doc.FactsByAlias[gold[^1]];
            var outsideFacts = doc.FactsByAlias[goldOutsideAlias];
            rows.Add(new Residual(
                documentId, packId, anchor, selected.PromptVersion, selected.RawCaptureSha256,
                gold[^1], predictedAliases[^1], predictedAliases.Length - gold.Length,
                goldOutside, predictedOutside, boundaryKind, gold.Length == 1 ? "SINGLETON" : "MULTIPART",
                selected.FirstOutsideRole, doc.FunctionByOccurrence[goldOutside], doc.G2AByOccurrence.GetValueOrDefault(goldOutside, "NOT_ISSUED"),
                Layout(endFacts, outsideFacts), selected.RawResponseSha256, selected.RawSseSha256));
        }

        Assert.Equal(10, rows.Count);
        Assert.All(rows, value => Assert.True(value.OverrunDistance > 0));
        FreezeArtifact.AssertJson(OutputRoot, "h2c-mixed-prompt-overextent-residual-audit.v1.json", new
        {
            schemaVersion = "v5-p6th2c-mixed-prompt-overextent-residual-audit-v1",
            status = "FROZEN_PROVIDER_FREE_RESIDUAL_DIAGNOSTIC_NOT_CLEAN_H2C_QUALIFICATION",
            authority = new
            {
                v1ManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(V1ManifestPath))),
                v2ManifestSha256 = Hash(File.ReadAllText(TestRepository.Path(V2ManifestPath))),
                goldAuthorities = Sources.Select(source => new { documentId = source.DocumentId, goldSha256 = authority[source.DocumentId].GoldSha256, sourceSha256 = authority[source.DocumentId].SourceSha256 }).ToArray(),
                providerCallsDuringAudit = 0,
                goldMutation = "NONE",
                rawCaptureMutation = "NONE",
                runtimeChanged = false,
            },
            scope = new
            {
                totalMixedContractValidLedgers = 31,
                residualOverextents = rows.Count,
                cleanQualificationStatus = "NOT_RUN",
                labels = new
                {
                    headingToBody = "A",
                    headingToNewHeading = "B",
                    headingToStructuredOrTable = "C",
                    headingToFurniture = "D",
                    falseSemanticGroupingOrOther = "E",
                },
            },
            aggregate = new
            {
                byBoundaryFamily = rows.GroupBy(value => value.BoundaryFamily).Select(group => new { family = group.Key, count = group.Count() }).OrderBy(value => value.family, StringComparer.Ordinal).ToArray(),
                byExtentShape = rows.GroupBy(value => value.ExtentShape).Select(group => new { shape = group.Key, count = group.Count() }).OrderBy(value => value.shape, StringComparer.Ordinal).ToArray(),
                byPromptVersion = rows.GroupBy(value => value.PromptVersion).Select(group => new { promptVersion = group.Key, count = group.Count() }).OrderBy(value => value.promptVersion, StringComparer.Ordinal).ToArray(),
                overrunDistance = new { min = rows.Min(value => value.OverrunDistance), max = rows.Max(value => value.OverrunDistance), mean = Math.Round(rows.Average(value => value.OverrunDistance), 3) },
            },
            residuals = rows.OrderBy(value => value.DocumentId, StringComparer.Ordinal).ThenBy(value => value.Anchor, StringComparer.Ordinal).ToArray(),
            interpretation = new
            {
                result = "RESIDUAL_FAMILY_DIAGNOSTIC_ONLY",
                boundaryFamilyRule = "B_IS_CANONICAL_GOLD_NEW_HEADING; C_D_A_E_USE_FROZEN_MODEL_OUTSIDE_ROLE_AND_SOURCE_FACTS_AS_DIAGNOSTIC_EVIDENCE_NOT_NEW_GOLD",
                prohibited = "NO_CLEAN_CAUSAL_CLAIM_NO_RUNTIME_PROMOTION_NO_GOLD_REWRITE",
            },
        });
    }

    private static Authority BuildAuthority(Source source)
    {
        var sha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source.PdfPath));
        var snapshotPath = TestRepository.Path($"{SnapshotRoot}/{sha}.json");
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(snapshotPath, source.DocumentId);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        using var f1Capture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/{source.F1Path}")));
        var f1Row = source.Shape switch
        {
            F1Shape.RawCapture => f1Capture.RootElement,
            F1Shape.ResultRow => f1Capture.RootElement.GetProperty("row"),
            F1Shape.ResultRows => f1Capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == source.DocumentId),
            _ => throw new InvalidDataException("unknown F1 shape"),
        };
        var pack = plan.Packs.Single(value => value.PackId == f1Row.GetProperty("packId").GetString());
        var prepared = PdfTotalOccurrenceRoleQualificationAdapter.PrepareFunctionMembershipF1(plan, pack,
            source.UsesCorrespondence ? Correspondences(pack) : new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal));
        var functions = PdfTotalOccurrenceRoleQualificationAdapter.ParseFunctionMembershipF1(prepared, f1Row.GetProperty("rawResponse").GetString()!)
            .Decisions.ToDictionary(value => value.OccurrenceId, value => value.Function.Wire(), StringComparer.Ordinal);
        var aliases = prepared.Request.Occurrences.ToDictionary(value => value.Id, value => value.Atom.Alias, StringComparer.Ordinal);
        using var g2a = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{G2ARoot}/{source.DocumentId}.raw-capture.v1.json")));
        var g2aMap = ParseG2A(g2a.RootElement.GetProperty("rawResponse").GetString()!);
        var facts = snapshot.RootElement.GetProperty("Evidence").EnumerateArray().ToDictionary(value => value.GetProperty("SourceAlias").GetString()!, EvidenceFacts.From, StringComparer.Ordinal);
        var gold = LoadGold(source.DocumentId);
        return new Authority(aliases, functions, g2aMap, facts, gold.GoldSha256, gold.SourceSha256, gold.Extents);
    }

    private static Selected Select(string documentId, string anchor, IReadOnlyList<string> issued, IReadOnlyList<string> clarifiedFiles)
    {
        var primary = Read(TestRepository.Path($"{PrimaryRoot}/{documentId}_{anchor}.raw-capture.v1.json"), "PRIMARY_PROMPT_V1");
        if (Parse(primary.RawResponse, anchor, issued, out var primaryDecision)) return primary with { Members = primaryDecision.Members, FirstOutsideRole = primaryDecision.OutsideRole };
        var path = Assert.Single(clarifiedFiles.Where(value =>
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(value));
            return document.RootElement.GetProperty("documentId").GetString() == documentId && document.RootElement.GetProperty("anchor").GetString() == anchor;
        }));
        var recovery = Read(path, "CLARIFIED_PROMPT_V2_RECOVERY");
        Assert.True(Parse(recovery.RawResponse, anchor, issued, out var decision));
        return recovery with { Members = decision.Members, FirstOutsideRole = decision.OutsideRole };
    }

    private static Selected Read(string path, string promptVersion)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var row = document.RootElement;
        return new Selected(promptVersion, Hash(bytes), row.GetProperty("rawResponse").GetString()!, row.GetProperty("rawResponseSha256").GetString()!,
            row.GetProperty("rawSseSha256").GetString()!, row.GetProperty("providerBodySha256").GetString()!, [], string.Empty);
    }

    private static bool Parse(string raw, string anchor, IReadOnlyList<string> issued, out Decision decision)
    {
        decision = new([], string.Empty);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.EnumerateObject().Count() != 1 || root.GetProperty("decisions").GetArrayLength() != 1) return false;
            var row = root.GetProperty("decisions")[0];
            if (row.EnumerateObject().Count() != 5 || row.GetProperty("anchor").GetString() != anchor) return false;
            var members = row.GetProperty("headingMembers").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (members.Length == 0 || !members.SequenceEqual(issued.Take(members.Length), StringComparer.Ordinal) || row.GetProperty("endOccurrence").GetString() != members[^1]) return false;
            var outside = row.GetProperty("firstOutsideOccurrence");
            var role = row.GetProperty("firstOutsideRole").GetString()!;
            var valid = members.Length == issued.Count
                ? outside.ValueKind == JsonValueKind.Null && role == "NO_VISIBLE_SUCCESSOR"
                : outside.GetString() == issued[members.Length] && role is "NEW_HEADING" or "BODY_CONTENT" or "PAGE_FURNITURE" or "TABLE_OR_STRUCTURED_CONTENT" or "OTHER_NON_HEADING";
            if (!valid) return false;
            decision = new(members, role);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private static string BoundaryFamily(Authority authority, string outside, string modelRole)
    {
        if (authority.GoldExtents.Any(value => value[0] == authority.AliasByOccurrence[outside])) return "B_HEADING_TO_NEW_HEADING";
        return modelRole switch
        {
            "BODY_CONTENT" => "A_HEADING_TO_BODY",
            "TABLE_OR_STRUCTURED_CONTENT" => "C_HEADING_TO_STRUCTURED_OR_TABLE",
            "PAGE_FURNITURE" => "D_HEADING_TO_FURNITURE",
            _ => "E_FALSE_SEMANTIC_GROUPING_OR_OTHER",
        };
    }

    private static object Layout(EvidenceFacts end, EvidenceFacts outside) => new
    {
        goldEndPage = end.Page,
        goldFirstOutsidePage = outside.Page,
        pageTransition = end.Page != outside.Page,
        goldEndVerticalPosition = end.VerticalPosition,
        goldFirstOutsideVerticalPosition = outside.VerticalPosition,
        fontSizeToBodyRatio = new { goldEnd = end.FontSizeToBodyRatio, firstOutside = outside.FontSizeToBodyRatio },
        boldRatio = new { goldEnd = end.BoldRatio, firstOutside = outside.BoldRatio },
        lineCount = new { goldEnd = end.LineCount, firstOutside = outside.LineCount },
        structuralScope = new { goldEnd = end.StructuralScope, firstOutside = outside.StructuralScope },
    };

    private static bool IsOverextent(IReadOnlyList<string> predicted, IReadOnlyList<string> gold) =>
        predicted.Count > gold.Count && gold.SequenceEqual(predicted.Take(gold.Count), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> Correspondences(PdfCandidateAuthorityPreparedPack pack)
    {
        var owned = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var candidates = pack.Universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in pack.Universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate) || !owned.Contains(candidate.Endpoint.Parts[0].Alias)) continue;
            var values = result.TryGetValue(candidate.Endpoint.Parts[0].Alias, out var old) ? old.ToList() : [];
            if (!values.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText)) values.Add(new(relation.TargetPage, relation.TargetText));
            result[candidate.Endpoint.Parts[0].Alias] = values;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseG2A(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(
            value => value.GetProperty("primary").GetString()!, value => value.GetProperty("anchor").GetString()!, StringComparer.Ordinal);
    }

    private static Gold LoadGold(string id)
    {
        var entry = FrozenHistoryGold.Entry(id);
        FrozenHistoryGold.RequireCapability(id, GoldCapability.Occurrence);
        using var document = FrozenHistoryGold.Resolve(id);
        var extents = document.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
            claim.GetProperty("sourceParts").EnumerateArray().Select(part => part.GetProperty("sourceAlias").GetString()!).ToArray()).ToArray();
        return new(entry.GoldSha256, entry.SourceSha256, extents);
    }

    private static string Key(JsonElement row) => $"{row.GetProperty("DocumentId").GetString()}|{row.GetProperty("PackId").GetString()}|{row.GetProperty("Anchor").GetString()}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private enum F1Shape { RawCapture, ResultRow, ResultRows }
    private sealed record Source(string DocumentId, string PdfPath, string F1Path, F1Shape Shape, bool UsesCorrespondence);
    private sealed record Gold(string GoldSha256, string SourceSha256, string[][] Extents);
    private sealed record Authority(IReadOnlyDictionary<string, string> AliasByOccurrence, IReadOnlyDictionary<string, string> FunctionByOccurrence,
        IReadOnlyDictionary<string, string> G2AByOccurrence, IReadOnlyDictionary<string, EvidenceFacts> FactsByAlias, string GoldSha256, string SourceSha256, string[][] GoldExtents);
    private sealed record EvidenceFacts(int Page, double VerticalPosition, double FontSizeToBodyRatio, double BoldRatio, int LineCount, string StructuralScope)
    {
        public static EvidenceFacts From(JsonElement row)
        {
            var style = row.GetProperty("StyleFacts");
            var location = row.GetProperty("LocationFacts");
            return new(location.GetProperty("page").GetInt32(), location.GetProperty("verticalPosition").GetDouble(),
                style.GetProperty("fontSizeToBodyRatio").GetDouble(), style.GetProperty("boldRatio").GetDouble(),
                style.GetProperty("lineCount").GetInt32(), row.GetProperty("StructuralScope").GetString()!);
        }
    }
    private sealed record Selected(string PromptVersion, string RawCaptureSha256, string RawResponse, string RawResponseSha256,
        string RawSseSha256, string ProviderBodySha256, IReadOnlyList<string> Members, string FirstOutsideRole);
    private sealed record Decision(IReadOnlyList<string> Members, string OutsideRole);
    private sealed record Residual(string DocumentId, string PackId, string Anchor, string PromptVersion, string RawCaptureSha256,
        string GoldEnd, string PredictedEnd, int OverrunDistance, string GoldFirstOutside, string? PredictedFirstOutside,
        string BoundaryFamily, string ExtentShape, string ModelSelectedOutsideRole, string F1AtGoldFirstOutside,
        string G2AAtGoldFirstOutside, object LayoutTransition, string RawResponseSha256, string RawSseSha256);
}
