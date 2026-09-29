using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Decomposes the 160 exact-text-binding refusals from the real 31-pack cohort at commit 3a4f69a,
/// offline, from the frozen provider responses alone. Never calls a provider, never reads Gold, never
/// touches the frozen raw cohort artifacts - it only reads them and writes a separate diagnosis file.
/// </summary>
public sealed class V5ExactTextBindingAuditTests
{
    private const string CohortRoot = "artifacts/v5-provider-cohort-31-windows";
    private const string SourceCommit = "3a4f69a194f1e0a4598bda8c5eeed06b7c57bb40";

    [Fact]
    public void Decompose_the_cohorts_exact_text_binding_refusals_offline()
    {
        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var cohort = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{CohortRoot}/preflight/cohort.v1.json"))).RootElement;
        var packRows = cohort.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => (row.GetProperty("DocumentId").GetString()!, row.GetProperty("PackId").GetString()!), row => row);

        var atomsByDoc = new Dictionary<string, IReadOnlyList<SemanticSourceAtom>>(StringComparer.Ordinal);
        IReadOnlyList<SemanticSourceAtom> AtomsFor(string documentId)
        {
            if (!atomsByDoc.TryGetValue(documentId, out var atoms))
            {
                var pdf = documentId == "SRC-089" ? SourcePdfCorpus.Src089 : SourcePdfCorpus.Src095;
                atomsByDoc[documentId] = atoms = V5PdfPreflightBuilder.LoadAtoms(TestRepository.Path(pdf));
            }
            return atoms;
        }

        var callDirs = Directory.GetDirectories(TestRepository.Path($"{CohortRoot}/provider/calls")).OrderBy(d => d, StringComparer.Ordinal).ToArray();
        Assert.Equal(31, callDirs.Length);

        var failureRows = new List<V5ExactTextFailureRow>();
        var usageCounts = new Dictionary<V5SourcePartUsage, int>();
        int partLevelExactTextRefusals = 0, unknownAliasRefusals = 0, sourceOrderRefusals = 0, otherRefusals = 0;
        int claimLevelExactTextRefusals = 0;
        int totalSourceParts = 0, aliasOnlyParts = 0, verbatimParts = 0, successfulVerbatimParts = 0, refusedVerbatimParts = 0, successfulAliasOnlyParts = 0;

        foreach (var callDir in callDirs)
        {
            var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json"))).RootElement;
            var documentId = call.GetProperty("documentId").GetString()!;
            var packId = call.GetProperty("packId").GetString()!;
            var atoms = AtomsFor(documentId);
            var atomsByAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
            var row = packRows[(documentId, packId)];
            var ownedAliases = row.GetProperty("ownedAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var visibleAliases = row.GetProperty("visibleAliases").EnumerateArray().Select(a => a.GetString()!).ToHashSet(StringComparer.Ordinal);
            var scope = ClaimBindingScope.Create(ownedAliases, visibleAliases);

            var contentPath = Path.Combine(callDir, "content.txt");
            var content = File.ReadAllText(contentPath);
            using var document = JsonDocument.Parse(content);
            var response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);

            // The authoritative, claim-level count: the exact same aggregate bind production and the
            // frozen cohort-result.v1.json used, filtered to the exact-text-binding family only.
            var aggregateBinding = ExactClaimBinderV2_1.Bind(packId, response.Claims, atoms, scope);
            claimLevelExactTextRefusals += aggregateBinding.Refusals.Values
                .Count(reason => V5BindingQualifier.RefusalFamily(reason) == V5BindingQualifier.FamilyExactTextBinding);

            for (var ordinal = 1; ordinal <= response.Claims.Count; ordinal++)
            {
                var proposal = response.Claims[ordinal - 1];
                foreach (var (endpointKind, endpoint) in new[] { ("subject", proposal.Subject), ("object", proposal.Object) })
                {
                    if (endpoint is null) continue;
                    for (var partIndex = 0; partIndex < endpoint.SourceParts.Count; partIndex++)
                    {
                        var part = endpoint.SourceParts[partIndex];
                        totalSourceParts++;
                        if (part.VerbatimText is null) { aliasOnlyParts++; } else { verbatimParts++; }

                        var canonical = ProviderSourcePartNormalization.ToCanonical(part);
                        var partBinding = SemanticSourcePartBinder.Bind(atoms, [canonical]);

                        if (partBinding.IsBound)
                        {
                            if (part.VerbatimText is null) { successfulAliasOnlyParts++; usageCounts.Increment(V5SourcePartUsage.ALIAS_ONLY); continue; }
                            successfulVerbatimParts++;
                            var bound = partBinding.Parts[0];
                            var usage = V5ExactTextBindingAnalyzer.ClassifyUsage(part.VerbatimText, atomsByAlias[part.SourceAlias].Text,
                                true, bound.Start, bound.End, null);
                            usageCounts.Increment(usage);
                            continue;
                        }

                        var family = V5BindingQualifier.RefusalFamily(partBinding.Reason ?? partBinding.Status.ToString());
                        if (family == V5BindingQualifier.FamilyUnknownAlias) { unknownAliasRefusals++; continue; }
                        if (family == V5BindingQualifier.FamilySourceOrderOrOverlap) { sourceOrderRefusals++; continue; }
                        if (family != V5BindingQualifier.FamilyExactTextBinding) { otherRefusals++; continue; }

                        partLevelExactTextRefusals++;
                        refusedVerbatimParts++;
                        var failureRow = V5ExactTextBindingAnalyzer.Analyze(
                            documentId, packId, ordinal, proposal.Predicate, endpointKind, partIndex,
                            part.SourceAlias, part.VerbatimText, part.Occurrence, part.LeftExactContext, part.RightExactContext,
                            partBinding.Reason ?? partBinding.Status.ToString(), atoms, visibleAliases);
                        failureRows.Add(failureRow);
                        usageCounts.Increment(V5ExactTextBindingAnalyzer.ClassifyUsage(
                            part.VerbatimText, atomsByAlias[part.SourceAlias].Text, false, null, null, failureRow.Category));
                    }
                }
            }
        }

        // Sanity: ownership/refinement/duplicate refusals are a separate concern from this audit and
        // are known to be zero of these families among the 160 (confirmed by the frozen cohort result).
        Assert.Equal(0, unknownAliasRefusals);
        Assert.Equal(0, sourceOrderRefusals);
        // The authoritative, claim-level count matches the frozen cohort-result.v1.json exactly.
        Assert.Equal(160, claimLevelExactTextRefusals);
        // The part-level count can exceed it: a claim whose subject AND object both fail contributes
        // one claim-level refusal but two part-level failure rows, by design (never merged).
        Assert.True(partLevelExactTextRefusals >= claimLevelExactTextRefusals,
            $"expected at least as many part-level failures ({partLevelExactTextRefusals}) as claim-level refusals ({claimLevelExactTextRefusals})");
        Assert.Equal(partLevelExactTextRefusals, failureRows.Count);

        var uniqueFailedParts = failureRows.Select(r => (r.SourceAlias, r.VerbatimText)).Distinct().Count();
        var uniqueAliasesAffected = failureRows.Select(r => r.SourceAlias).Distinct(StringComparer.Ordinal).Count();
        var uniqueClusters = failureRows
            .Select(r => (r.DocumentId, r.SourceAlias, Normalized: V5ExactTextBindingAnalyzer.CollapseWhitespace(r.VerbatimText ?? "").ToUpperInvariant()))
            .Distinct()
            .Count();

        var taxonomy = failureRows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<string, int> TaxonomyReport() => Enum.GetValues<V5ExactTextFailureCategory>()
            .ToDictionary(c => c.ToString(), c => taxonomy.GetValueOrDefault(c), StringComparer.Ordinal);

        var wouldBindAsWholeAlias = failureRows.Count(r => r.Counterfactuals.WouldBindAsWholeAlias);
        var wouldBindWithExactMultipart = failureRows.Count(r => r.Counterfactuals.WouldBindWithExactMultipart);
        var wouldBindWithExistingDisambiguation = failureRows.Count(r => r.Counterfactuals.WouldBindWithExistingDisambiguation);
        var whitespaceOnly = failureRows.Count(r => r.Counterfactuals.WouldMatchAfterWhitespaceCollapse || r.Counterfactuals.WouldMatchAfterWhitespaceRemoval);
        var unicodeOnly = failureRows.Count(r => r.Counterfactuals.WouldMatchAfterNfc || r.Counterfactuals.WouldMatchAfterNfkc);

        var topPacks = failureRows.GroupBy(r => (r.DocumentId, r.PackId)).OrderByDescending(g => g.Count()).Take(5)
            .Select(g => new { documentId = g.Key.DocumentId, packId = g.Key.PackId, refusalCount = g.Count() }).ToArray();
        var topAliases = failureRows.GroupBy(r => (r.DocumentId, r.SourceAlias)).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => new { documentId = g.Key.DocumentId, alias = g.Key.SourceAlias, refusalCount = g.Count() }).ToArray();

        var report = new
        {
            schemaVersion = "v5-exact-text-binding-audit-v1",
            sourceCohortCommit = SourceCommit,
            providerCalls = 0,
            goldRead = false,
            binderChanged = false,
            promptChanged = false,
            schemaChanged = false,

            rawExactTextRefusals = claimLevelExactTextRefusals,
            partLevelExactTextFailures = partLevelExactTextRefusals,
            uniqueFailedSourceParts = uniqueFailedParts,
            uniqueAliasesAffected,
            uniqueNormalizedRootCauseClusters = uniqueClusters,

            taxonomy = TaxonomyReport(),
            faultDomain = failureRows.GroupBy(r => r.FaultDomain.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),

            counterfactual = new
            {
                wouldBindAsWholeAlias,
                wouldBindWithExactMultipart,
                wouldBindWithExistingDisambiguation,
                whitespaceOnly,
                unicodeOnly,
            },

            sourcePartsCensus = new
            {
                totalSourceParts,
                aliasOnlyParts,
                verbatimTextParts = verbatimParts,
                successfulAliasOnlyParts,
                successfulVerbatimParts,
                refusedVerbatimParts,
                usageBreakdown = Enum.GetValues<V5SourcePartUsage>().ToDictionary(u => u.ToString(), u => usageCounts.GetValueOrDefault(u), StringComparer.Ordinal),
            },

            concentration = new { topPacksByRefusalCount = topPacks, topAliasesByRefusalCount = topAliases },

            rows = failureRows.Select(r => new
            {
                r.DocumentId, r.PackId, r.ProposalOrdinal, r.Predicate, r.Endpoint, r.PartIndex,
                r.SourceAlias, r.VerbatimText, r.Occurrence, r.LeftExactContext, r.RightExactContext,
                r.RefusalReason, r.SourceAtomText, r.AtomOrdinal,
                neighboringVisibleAtoms = r.NeighboringVisibleAtoms.Select(n => new { alias = n.Alias, text = n.Text }),
                category = r.Category.ToString(),
                faultDomain = r.FaultDomain.ToString(),
                r.DiagnosticReason,
                counterfactuals = r.Counterfactuals,
            }).ToArray(),
        };

        var path = TestRepository.Path($"{CohortRoot}/diagnosis/exact-text-binding-audit.v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, Encoding.UTF8.GetString(bytes) + Environment.NewLine, new UTF8Encoding(false));

        // Determinism: re-running the same offline analysis over the same frozen inputs reproduces the same bytes.
        var repeatBytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(bytes, repeatBytes);
    }

    // ---- focused unit tests for the analyzer itself, deterministic synthetic fixtures ----------

    [Fact]
    public void Wrong_alias_text_is_detected_when_the_quote_belongs_to_another_visible_alias()
    {
        var atoms = Atoms(("L1", "S1", "Alpha heading"), ("L2", "S2", "-------"));
        var row = V5ExactTextBindingAnalyzer.Analyze("DOC", "PACK", 1, "DESCRIBES", "subject", 0,
            "L1", "-------", null, null, null, "'L1' does not contain that text", atoms, Visible(atoms));
        Assert.Equal(V5ExactTextFailureCategory.WRONG_ALIAS_TEXT, row.Category);
        Assert.Equal(V5ExactTextFaultDomain.MODEL_REFERENCE, row.FaultDomain);
        Assert.Contains("L2", row.DiagnosticReason);
    }

    [Fact]
    public void Multi_atom_overquote_finds_the_minimal_alias_sequence()
    {
        var atoms = Atoms(
            ("L1", "S1", "Article 2. Tasks and powers ofthe Ministry ofInformation and Communications in performing"),
            ("L2", "S2", "the state management of publication activities"));
        var quote = "Article 2. Tasks and powers of the Ministry of Information and Communications in performing the state management of publication activities";
        var row = V5ExactTextBindingAnalyzer.Analyze("DOC", "PACK", 1, "STRUCTURAL_REGION", "subject", 0,
            "L1", quote, null, null, null, "'L1' does not contain that text", atoms, Visible(atoms));
        Assert.Equal(V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE, row.Category);
        Assert.Equal(["L1", "L2"], row.Counterfactuals.ExactMultipartAliasSequence);
        Assert.True(row.Counterfactuals.WouldBindWithExactMultipart);
    }

    [Fact]
    public void Extraction_spacing_difference_is_detected_without_modifying_anything()
    {
        var atoms = Atoms(("L1", "S1", "Article 1. Scope ofregulation and subjects ofapplication"));
        var row = V5ExactTextBindingAnalyzer.Analyze("DOC", "PACK", 1, "STRUCTURAL_REGION", "subject", 0,
            "L1", "Article 1. Scope of regulation and subjects of application", null, null, null,
            "'L1' does not contain that text", atoms, Visible(atoms));
        Assert.Equal(V5ExactTextFailureCategory.EXTRACTION_SPACING_DIFFERENCE, row.Category);
        Assert.Equal(V5ExactTextFaultDomain.SOURCE_REPRESENTATION, row.FaultDomain);
        Assert.True(row.Counterfactuals.WouldMatchAfterWhitespaceRemoval);
    }

    [Fact]
    public void Repeated_substring_without_disambiguation_is_its_own_category_not_absence()
    {
        var atoms = Atoms(("L1", "S1", "Article 1 refers to Article 1 again"));
        var row = V5ExactTextBindingAnalyzer.Analyze("DOC", "PACK", 1, "REFERENCES", "object", 0,
            "L1", "Article 1", null, null, null, "'L1' contains that text 2 times and nothing says which", atoms, Visible(atoms));
        Assert.Equal(V5ExactTextFailureCategory.REPEATED_SUBSTRING_WITHOUT_DISAMBIGUATION, row.Category);
        Assert.Equal(V5ExactTextFaultDomain.AMBIGUOUS_EXACT_SELECTION, row.FaultDomain);
        Assert.True(row.Counterfactuals.WouldBindWithExistingDisambiguation);
    }

    [Fact]
    public void Completely_wrong_text_is_distinguished_from_a_recoverable_mismatch()
    {
        var atoms = Atoms(("L1", "S1", "Article 5. Publication licensing procedure"));
        var row = V5ExactTextBindingAnalyzer.Analyze("DOC", "PACK", 1, "DESCRIBES", "subject", 0,
            "L1", "Completely unrelated sentence about something else entirely", null, null, null,
            "'L1' does not contain that text", atoms, Visible(atoms));
        Assert.Equal(V5ExactTextFailureCategory.COMPLETELY_WRONG_TEXT, row.Category);
    }

    [Fact]
    public void Whole_atom_quote_is_redundant_and_a_strict_subset_is_necessary()
    {
        Assert.Equal(V5SourcePartUsage.VERBATIM_REDUNDANT_WHOLE_ATOM_QUOTE,
            V5ExactTextBindingAnalyzer.ClassifyUsage("Alpha", "Alpha", true, 0, 5, null));
        Assert.Equal(V5SourcePartUsage.VERBATIM_NECESSARY_SUBSTRING,
            V5ExactTextBindingAnalyzer.ClassifyUsage("Al", "Alpha", true, 0, 2, null));
        Assert.Equal(V5SourcePartUsage.ALIAS_ONLY, V5ExactTextBindingAnalyzer.ClassifyUsage(null, "Alpha", true, 0, 5, null));
    }

    private static SemanticSourceAtom[] Atoms(params (string Alias, string SourceId, string Text)[] items) =>
        items.Select((item, index) => new SemanticSourceAtom(item.Alias, item.SourceId, index + 1, 1, index + 1, 0, item.Text)).ToArray();

    private static HashSet<string> Visible(IEnumerable<SemanticSourceAtom> atoms) => atoms.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
}

file static class DictionaryExtensions
{
    public static void Increment<TKey>(this Dictionary<TKey, int> counts, TKey key) where TKey : notnull =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
