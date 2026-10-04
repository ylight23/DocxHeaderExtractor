using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free P6T-B audit of the two immutable P6T-A role ledgers.</summary>
public sealed class V5P6TBAnchorRoleAuditTests
{
    private const string CaptureRoot = "artifacts/v5-p6t-total-occurrence-role/p6ta-two-pack-canary";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldRoot = "eval/a99-closed-loop/gold-current/documents";
    private const string DiagnosisPath = "artifacts/v5-p6s-candidate-authority/p6sg-preprojection-diagnosis/strict-exact-residual-decomposition.v1.json";

    private sealed record Prepared(string DocumentId, PdfCandidateAuthorityDocumentPlan Plan, PdfTotalRolePreparedPack Pack,
        IReadOnlyDictionary<string, V5OccurrenceRoleV1> Roles);
    private sealed record Part(string Alias, int Start, int End);

    [Fact]
    public void P6TB_audits_gold_starts_continuations_and_53_reviewed_toc_negatives_without_provider()
    {
        var capturePath = TestRepository.Path($"{CaptureRoot}/result.v1.json");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        Assert.Equal(2, capture.RootElement.GetProperty("providerCalls").GetInt32());
        Assert.False(capture.RootElement.GetProperty("goldRead").GetBoolean());
        Assert.Equal(0, capture.RootElement.GetProperty("retry").GetInt32());

        var prepared = new[] { Prepare("SRC-089", SourcePdfCorpus.Src089), Prepare("SRC-095", SourcePdfCorpus.Src095) };
        var byDocument = prepared.ToDictionary(value => value.DocumentId, StringComparer.Ordinal);
        var toc = ReadReviewedToc();

        var src089 = Audit089(byDocument["SRC-089"]);
        var src095 = Audit095(byDocument["SRC-095"], toc);

        var output = new
        {
            schemaVersion = "v5-p6tb-anchor-role-audit-v1",
            execution = new { providerCalls = 0, sourceCapture = $"{CaptureRoot}/result.v1.json", sourceCaptureSha256 = Hash(File.ReadAllBytes(capturePath)), goldMutation = "NONE", runtimeChanged = false, rawResponseCopied = false },
            authority = new { purpose = "ROLE_AUDIT_ONLY", p6taViability = "PROVISIONALLY_SUPPORTED", pass2 = "BLOCKED", accuracyScore = "NOT_COMPUTED" },
            src089, src095,
        };
        FreezeArtifact.AssertJson("artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit", "anchor-role-audit.v1.json", output);
    }

    private static Prepared Prepare(string id, string source)
    {
        var sourceHash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceHash}.json"), id);
        var sourcePack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var pack = PdfTotalOccurrenceRoleQualificationAdapter.Prepare(plan, sourcePack);
        using var capture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CaptureRoot}/result.v1.json")));
        var row = capture.RootElement.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == id);
        Assert.Equal(pack.ProviderRequestHash, row.GetProperty("providerRequestHash").GetString());
        var raw = row.GetProperty("rawResponse").GetString()!;
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
        using var response = JsonDocument.Parse(raw);
        var parsed = PdfTotalOccurrenceRoleQualificationAdapter.Parse(pack, raw);
        return new Prepared(id, plan, pack, parsed.Decisions.ToDictionary(value => value.OccurrenceId, value => value.Role, StringComparer.Ordinal));
    }

    private static object Audit089(Prepared prepared)
    {
        var atoms = prepared.Plan.SourceAtoms.ToDictionary(value => value.Alias, StringComparer.Ordinal);
        var gold = ReadGold("SRC-089", atoms);
        var starts = new List<object>();
        var continuationRoleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var goldUnits = 0; var goldStartsVisible = 0; var falseContinuationStarts = 0;
        foreach (var claim in gold)
        {
            if (!prepared.Pack.Request.Occurrences.Any(value => value.Atom.Alias == claim.Parts[0].Alias)) continue;
            var ids = claim.Parts.Select(part => prepared.Pack.Request.Occurrences.Single(value => value.Atom.Alias == part.Alias).Id).ToArray();
            goldUnits++; goldStartsVisible++;
            var startRole = prepared.Roles[ids[0]].ToString();
            var continuationRoles = ids.Skip(1).Select(id => prepared.Roles[id].ToString()).ToArray();
            foreach (var role in continuationRoles) continuationRoleCounts[role] = continuationRoleCounts.GetValueOrDefault(role) + 1;
            falseContinuationStarts += continuationRoles.Count(role => role == nameof(V5OccurrenceRoleV1.HEADING_START));
            starts.Add(new { primary = claim.Parts[0].Alias, partCount = ids.Length, startRole, continuationRoles });
        }
        return new
        {
            pack = prepared.Pack.SourcePack.PackId, issuedOccurrences = prepared.Pack.Request.Occurrences.Count,
            modelRoleCounts = prepared.Roles.Values.GroupBy(value => value.ToString()).OrderBy(value => value.Key).ToDictionary(value => value.Key, value => value.Count()),
            goldMultipartUnitsInPack = goldUnits, goldStartsWithIssuedAnchor = goldStartsVisible,
            continuationRoleCounts, falseContinuationStarts, goldUnits = starts,
        };
    }

    private static object Audit095(Prepared prepared, IReadOnlyList<(string PackId, string CandidateId, string Identity, string PrimaryAlias)> toc)
    {
        var pack = prepared.Pack.SourcePack;
        var selected = toc.Where(value => value.PackId == pack.PackId).ToArray();
        Assert.Equal(53, selected.Length);
        var rows = selected.Select(value =>
        {
            var occurrence = prepared.Pack.Request.Occurrences.Single(item => item.Atom.Alias == value.PrimaryAlias);
            return new { value.CandidateId, occurrence = occurrence.Id, role = prepared.Roles[occurrence.Id].ToString() };
        }).ToArray();
        return new
        {
            pack = pack.PackId, reviewedTocTargets = selected.Length,
            roleCounts = rows.GroupBy(value => value.role).OrderBy(value => value.Key).ToDictionary(value => value.Key, value => value.Count()),
            representationStart = rows.Count(value => value.role == nameof(V5OccurrenceRoleV1.REPRESENTATION_START)),
            headingStart = rows.Count(value => value.role == nameof(V5OccurrenceRoleV1.HEADING_START)),
            other = rows.Count(value => value.role == nameof(V5OccurrenceRoleV1.OTHER)), rows,
        };
    }

    private static IReadOnlyList<(string PackId, string CandidateId, string Identity, string PrimaryAlias)> ReadReviewedToc()
    {
        using var diagnosis = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(DiagnosisPath)));
        return diagnosis.RootElement.GetProperty("falsePositives").EnumerateArray()
            .Where(item => item.GetProperty("DocumentId").GetString() == "SRC-095" && item.GetProperty("reason").GetString() == "REVIEWED_TOC_NAVIGATION_NONHEADING")
            .Select(item =>
            {
                var parts = item.GetProperty("parts").EnumerateArray().ToArray();
                return (item.GetProperty("PackId").GetString()!, item.GetProperty("CandidateId").GetString()!, item.GetProperty("Identity").GetString()!, parts[0].GetProperty("Alias").GetString()!);
            }).ToArray();
    }

    private static List<(string Identity, IReadOnlyList<Part> Parts)> ReadGold(string documentId, IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldRoot}/{documentId}.gold.v1.json")));
        return json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
            var binding = SemanticSourcePartBinder.Bind(atoms.Values.OrderBy(value => value.Ordinal).ToArray(), sourceParts);
            Assert.True(binding.IsBound, binding.Reason);
            var parts = binding.Parts.Select(value => new Part(value.Alias, value.Start, value.End)).ToArray();
            return (string.Join("|", parts.Select(value => $"{value.Alias}:{value.Start}-{value.End}")), (IReadOnlyList<Part>)parts);
        }).ToList();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
