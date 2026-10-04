using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>Provider-free C→D transition audit.  It tests whether adding UNRESOLVED changed roles.</summary>
public sealed class V5P6TDDeltaTests
{
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string CPath = "artifacts/v5-p6t-total-occurrence-role/p6tc-correspondence-evidence/result.v1.json";
    private const string DPath = "artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention/result.v1.json";
    private const string BPath = "artifacts/v5-p6t-total-occurrence-role/p6tb-anchor-role-audit/anchor-role-audit.v1.json";
    private const string OutputRoot = "artifacts/v5-p6t-total-occurrence-role/p6td-explicit-abstention";

    [Fact]
    public void P6TDDelta_freezes_transition_matrix_and_authority_probes()
    {
        using var c = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(CPath)));
        using var d = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(DPath)));
        using var b = JsonDocument.Parse(File.ReadAllText(TestRepository.Path(BPath)));
        var rows = new List<object>();
        foreach (var documentId in new[] { "SRC-089", "SRC-095" })
        {
            var cRoles = Decisions(c.RootElement, documentId);
            var dRoles = Decisions(d.RootElement, documentId);
            Assert.Equal(cRoles.Keys.OrderBy(value => value), dRoles.Keys.OrderBy(value => value));
            Assert.DoesNotContain(dRoles.Values, value => value == "UNRESOLVED");
            var transitions = cRoles.Keys.GroupBy(id => $"{cRoles[id]}->{dRoles[id]}").OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count());
            rows.Add(new { documentId, total = cRoles.Count, p6tc = Counts(cRoles), p6td = Counts(dRoles), transitions });

            if (documentId == "SRC-095")
            {
                var toc = b.RootElement.GetProperty("src095").GetProperty("rows").EnumerateArray().Select(value => value.GetProperty("occurrence").GetString()!).ToArray();
                Assert.Equal(53, toc.Length);
                Assert.All(toc, id => Assert.Equal("HEADING_START", cRoles[id]));
                Assert.All(toc, id => Assert.Equal("HEADING_START", dRoles[id]));
                rows.Add(new { documentId, probe = "REVIEWED_TOC_HARD_NEGATIVES", count = toc.Length, cHeadingStart = toc.Count(id => cRoles[id] == "HEADING_START"), dHeadingStart = toc.Count(id => dRoles[id] == "HEADING_START"), dRepresentationStart = toc.Count(id => dRoles[id] == "REPRESENTATION_START"), dOther = toc.Count(id => dRoles[id] == "OTHER"), dUnresolved = toc.Count(id => dRoles[id] == "UNRESOLVED") });
            }
            else
            {
                var aliases = new[] { "L0006:S0", "L0007:S0", "L0008:S0", "L0014:S0", "L0015:S0", "L0016:S0", "L0024:S0", "L0025:S0", "L0051:S0", "L0052:S0", "L0080:S0", "L0081:S0" };
                var occurrenceByAlias = OccurrenceByAlias(documentId);
                var roots = new[] { "L0006:S0", "L0014:S0", "L0016:S0", "L0024:S0", "L0051:S0", "L0080:S0" };
                var continuations = aliases.Except(roots, StringComparer.Ordinal).ToArray();
                Assert.Equal(6, roots.Length);
                Assert.Equal(6, roots.Distinct(StringComparer.Ordinal).Count());
                Assert.All(roots, alias => Assert.Equal("HEADING_START", cRoles[occurrenceByAlias[alias]]));
                Assert.All(roots, alias => Assert.Equal("HEADING_START", dRoles[occurrenceByAlias[alias]]));
                rows.Add(new
                {
                    documentId, probe = "GOLD_MULTIPART_ANCHORS_AND_CONTINUATIONS", roots = roots.Length, continuationAtoms = continuations.Length,
                    rootTransitions = roots.ToDictionary(alias => alias, alias => new { occurrence = occurrenceByAlias[alias], p6tc = cRoles[occurrenceByAlias[alias]], p6td = dRoles[occurrenceByAlias[alias]] }, StringComparer.Ordinal),
                    continuationTransitions = continuations.ToDictionary(alias => alias, alias => new { occurrence = occurrenceByAlias[alias], p6tc = cRoles[occurrenceByAlias[alias]], p6td = dRoles[occurrenceByAlias[alias]] }, StringComparer.Ordinal),
                });
            }
        }

        FreezeArtifact.AssertJson(OutputRoot, "p6td-delta-audit.v1.json", new
        {
            schemaVersion = "v5-p6td-delta-audit-v1", providerCalls = 0, goldRead = false, rawResponsesCopied = false,
            sourceCaptures = new { p6tc = CPath, p6td = DPath }, matchedVariable = "ONLY_EXPLICIT_UNRESOLVED_ROLE_AND_WORDING",
            unresolved = new { p6tc = 0, p6td = 0 }, rows,
            conclusion = "EXPLICIT_ABSTENTION_NOT_USED; TRANSITION_MATRIX_AND_ROLE_PROBES_FROZEN; ROLE_FRAMING_REMAINS_OPEN",
        });
    }

    private static Dictionary<string, string> Decisions(JsonElement root, string documentId)
    {
        var row = root.GetProperty("rows").EnumerateArray().Single(value => value.GetProperty("documentId").GetString() == documentId);
        using var response = JsonDocument.Parse(row.GetProperty("rawResponse").GetString()!);
        return response.RootElement.GetProperty("decisions").EnumerateArray().ToDictionary(value => value.GetProperty("occurrence").GetString()!, value => value.GetProperty("role").GetString()!, StringComparer.Ordinal);
    }

    private static Dictionary<string, int> Counts(IReadOnlyDictionary<string, string> values) => values.GroupBy(value => value.Value).OrderBy(value => value.Key).ToDictionary(value => value.Key, value => value.Count(), StringComparer.Ordinal);

    private static Dictionary<string, string> OccurrenceByAlias(string documentId)
    {
        var source = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
        var hash = CanonicalSemanticSourceHash.Compute(TestRepository.Path(source));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{hash}.json"), documentId);
        var pack = plan.Packs.Single(value => value.PackId.EndsWith("PACK_001", StringComparison.Ordinal));
        var ordered = pack.OwnedAliases.Select(alias => plan.SourceAtoms.Single(atom => atom.Alias == alias)).OrderBy(value => value.Ordinal).ThenBy(value => value.Alias, StringComparer.Ordinal).ToArray();
        return ordered.Select((atom, index) => new { atom.Alias, Id = $"O{index + 1}" }).ToDictionary(value => value.Alias, value => value.Id, StringComparer.Ordinal);
    }
}
