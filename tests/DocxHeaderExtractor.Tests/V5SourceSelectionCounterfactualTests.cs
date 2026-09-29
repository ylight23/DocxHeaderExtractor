using System.Text;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// A provider-free counterfactual replay of the frozen 31-pack cohort (same raw inputs as
/// <see cref="V5ExactTextBindingAuditTests"/>): for every source part, asks only "does a cheaper,
/// mechanically-justified wire shape still bind to the same alias/span", never "would this look
/// semantically better". No frozen response is mutated; every row's outcome is recomputed from the
/// atoms and the frozen provider text alone.
/// <list type="bullet">
/// <item>Class A - the 502 already-successful whole-atom verbatim quotes: removing verbatimText and
/// binding sourceAlias alone must land on the exact same span. This is a structural guarantee of
/// <see cref="SemanticSourcePartBinder"/> (WholeAlias always resolves to [0, atom length)) - this
/// test still replays every one of the 502 rather than asserting it from the definition alone.</item>
/// <item>Class B - failed rows whose quote, under some mechanical transform, is EQUAL to the
/// complete named atom (not merely contained in it - a proper substring match is never counted
/// here, since dropping verbatimText there would silently widen the selection). Only these are
/// safely alias-only representable.</item>
/// <item>Class C - the true multipart rows from the corrected diagnosis: verifies the diagnosed
/// minimal alias sequence, as multiple WholeAlias sourceParts, binds.</item>
/// <item>Z - whatever remains: WRONG_ALIAS_TEXT, genuine substrings, paraphrase, completely wrong
/// text, unresolved repeated substrings, and anything else. These are hard negatives the wire policy
/// does not and should not resolve.</item>
/// </list>
/// This is representation opportunity, not a prediction of future model behavior.
/// </summary>
public sealed class V5SourceSelectionCounterfactualTests
{
    private const string CohortRoot = "artifacts/v5-provider-cohort-31-windows";

    private sealed record PackCounterfactualProfile(string DocumentId, string PackId, int ClassB, int ClassC, int Z, int OldExactTextRefusals);

    private sealed record CounterfactualWalkResult(
        int ClaimLevelExactTextRefusals, int PartLevelExactTextRefusals,
        int ClassATotal, int ClassAReplayedIdentical, int ClassBTotal, int ClassCTotal, int ClassCVerified,
        IReadOnlyList<object> ZRows, IReadOnlyList<PackCounterfactualProfile> PackProfiles);

    [Fact]
    public void Counterfactual_replay_of_the_frozen_cohort_finds_the_cheapest_mechanically_justified_wire_shape()
    {
        var result = Walk();

        Assert.Equal(160, result.ClaimLevelExactTextRefusals);
        Assert.Equal(169, result.PartLevelExactTextRefusals);
        Assert.Equal(502, result.ClassATotal);
        Assert.Equal(result.ClassATotal, result.ClassAReplayedIdentical);
        Assert.Equal(20, result.ClassCTotal);
        Assert.Equal(result.ClassCTotal, result.ClassCVerified);
        Assert.Equal(result.PartLevelExactTextRefusals, result.ClassBTotal + result.ClassCTotal + result.ZRows.Count);

        var report = new
        {
            schemaVersion = "v5-source-selection-remediation-counterfactual-v1",
            sourceCohortCommit = "3a4f69a194f1e0a4598bda8c5eeed06b7c57bb40",
            providerCalls = 0,
            goldRead = false,
            note = "representation opportunity over the frozen cohort, not a prediction of future model behavior",
            claimLevelExactTextRefusals = result.ClaimLevelExactTextRefusals,
            partLevelExactTextFailures = result.PartLevelExactTextRefusals,
            classA_redundantSuccessfulWholeAtomQuotes = new { removable = result.ClassAReplayedIdentical, of = result.ClassATotal },
            classB_failedSameAtomWholeSelectionsAliasOnlyRepresentable = result.ClassBTotal,
            classC_trueMultipartMechanicallyRepresentable = new { verified = result.ClassCVerified, of = result.ClassCTotal },
            z_remainingExactTextFailuresNotSolvedByWirePolicy = new
            {
                count = result.ZRows.Count,
                byCategory = result.ZRows.GroupBy(r => (string)r.GetType().GetProperty("category")!.GetValue(r)!)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                rows = result.ZRows,
            },
        };

        WriteJson($"{CohortRoot}/diagnosis/source-selection-remediation-counterfactual.v1.json", report);
    }

    /// <summary>
    /// Selects, purely from the frozen counterfactual measurement above (never Gold), up to three
    /// packs that best exercise the failure modes the wire policy targets: the pack with the most
    /// alias-only-safe same-atom rows (Class B), the pack with the most true multipart rows (Class
    /// C), and a third, mixed pack carrying both. For each, freezes the NEW (post-policy) semantic
    /// request hash, provider request hash, exact body bytes and completion budget - a preflight
    /// only. <see cref="V5CanaryGate"/> still requires an explicit authorization flag this test never
    /// sets before any of these three could ever be sent to a provider.
    /// </summary>
    [Fact]
    public void Freeze_a_targeted_3_pack_remediation_canary_from_diagnostic_evidence()
    {
        var result = Walk();
        var profiles = result.PackProfiles.Where(p => p.ClassB + p.ClassC + p.Z > 0).ToArray();

        var byClassBDesc = profiles.OrderByDescending(p => p.ClassB).ThenBy(p => p.DocumentId, StringComparer.Ordinal).ThenBy(p => p.PackId, StringComparer.Ordinal).ToArray();
        var packSameAtomHeavy = byClassBDesc.First();

        var byClassCDesc = profiles.OrderByDescending(p => p.ClassC).ThenBy(p => p.DocumentId, StringComparer.Ordinal).ThenBy(p => p.PackId, StringComparer.Ordinal).ToArray();
        var packMultipartHeavy = byClassCDesc.First(p => !(p.DocumentId == packSameAtomHeavy.DocumentId && p.PackId == packSameAtomHeavy.PackId));

        // "Mixed": carries both Class B and Class C, distinct from the two picks above, tie-broken
        // deterministically by the smallest |B-C| imbalance then by (documentId, packId).
        var packMixed = profiles
            .Where(p => p.ClassB > 0 && p.ClassC > 0 &&
                !(p.DocumentId == packSameAtomHeavy.DocumentId && p.PackId == packSameAtomHeavy.PackId) &&
                !(p.DocumentId == packMultipartHeavy.DocumentId && p.PackId == packMultipartHeavy.PackId))
            .OrderBy(p => Math.Abs(p.ClassB - p.ClassC))
            .ThenByDescending(p => p.ClassB + p.ClassC)
            .ThenBy(p => p.DocumentId, StringComparer.Ordinal).ThenBy(p => p.PackId, StringComparer.Ordinal)
            .FirstOrDefault()
            // No pack independently carries both - fall back to the next-largest overall failure profile.
            ?? profiles
                .Where(p => !(p.DocumentId == packSameAtomHeavy.DocumentId && p.PackId == packSameAtomHeavy.PackId) &&
                    !(p.DocumentId == packMultipartHeavy.DocumentId && p.PackId == packMultipartHeavy.PackId))
                .OrderByDescending(p => p.ClassB + p.ClassC + p.Z)
                .ThenBy(p => p.DocumentId, StringComparer.Ordinal).ThenBy(p => p.PackId, StringComparer.Ordinal)
                .First();

        var selection = new[]
        {
            (Role: "same-atom-normalization-heavy", Profile: packSameAtomHeavy),
            (Role: "true-multipart-heavy", Profile: packMultipartHeavy),
            (Role: "mixed", Profile: packMixed),
        };
        Assert.Equal(3, selection.Length);
        Assert.Equal(selection.Select(s => (s.Profile.DocumentId, s.Profile.PackId)).Distinct().Count(), selection.Length);

        var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
        var envelope = new V5ProviderEnvelope("qwen/qwen3.7-flash", "alibaba", "none", true, "json_object", 300) { UsageInclude = true };
        var builtByDoc = new[] { ("SRC-089", SourcePdfCorpus.Src089), ("SRC-095", SourcePdfCorpus.Src095) }
            .ToDictionary(item => item.Item1, item => V5PdfPreflightBuilder.BuildV2_1(
                TestRepository.Path(item.Item2), item.Item1, contract, SemanticEvidencePackingPolicies.PdfResourceBoundedP05.PolicyId, envelope));

        var frozen = selection.Select(item =>
        {
            var built = builtByDoc[item.Profile.DocumentId];
            var pack = built.Requests.Single(request => request.PackId == item.Profile.PackId);
            return new
            {
                role = item.Role,
                documentId = item.Profile.DocumentId,
                packId = item.Profile.PackId,
                selectionRationale = item.Role switch
                {
                    "same-atom-normalization-heavy" => $"most Class-B (alias-only-safe same-atom) rows of any pack in the frozen counterfactual replay: {item.Profile.ClassB}",
                    "true-multipart-heavy" => $"most Class-C (true multipart) rows of any pack in the frozen counterfactual replay: {item.Profile.ClassC}",
                    _ => $"carries both Class-B ({item.Profile.ClassB}) and Class-C ({item.Profile.ClassC}) rows in the frozen counterfactual replay",
                },
                oldExactTextRefusalProfile = new { classB = item.Profile.ClassB, classC = item.Profile.ClassC, z = item.Profile.Z, totalExactTextRefusals = item.Profile.OldExactTextRefusals },
                ownedAliasCount = pack.OwnedAliases.Count,
                visibleAliasCount = pack.VisibleAliases.Count,
                newSemanticRequestHash = pack.Request.RequestHash,
                newSemanticRequestBytes = pack.Request.Utf8Bytes,
                newProviderRequestHash = pack.ProviderRequestHash,
                newProviderRequestBytes = pack.ProviderRequestBytes,
                newMaxCompletionTokens = pack.MaxCompletionTokens,
            };
        }).ToArray();

        var report = new
        {
            schemaVersion = "v5-source-selection-remediation-canary-v1",
            sourceCohortCommit = "3a4f69a194f1e0a4598bda8c5eeed06b7c57bb40",
            protocolVersion = V5Protocol.ClaimSchemaVersionV2_1,
            composerVersion = V5SemanticRequestComposerV2_1.Version,
            selectionSourcedFrom = "frozen 31-pack counterfactual replay measurement, not Gold",
            packs = frozen,
            providerCalls = 0,
            goldRead = false,
            providerExecutionAuthorized = false,
        };

        Assert.Throws<InvalidOperationException>(() => V5CanaryGate.Authorize(selection.Length, providerExecutionAuthorized: false));

        WriteJson($"{CohortRoot}/diagnosis/source-selection-remediation-canary-selection.v1.json", report);
    }

    private static CounterfactualWalkResult Walk()
    {
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
        if (callDirs.Length != 31) throw new InvalidOperationException($"expected-31-frozen-calls:{callDirs.Length}");

        int claimLevelExactTextRefusals = 0, partLevelExactTextRefusals = 0;
        int classATotal = 0, classAReplayedIdentical = 0;
        int classBTotal = 0, classCTotal = 0, classCVerified = 0;
        var zRows = new List<object>();
        var packProfiles = new List<PackCounterfactualProfile>();

        foreach (var callDir in callDirs)
        {
            var call = JsonDocument.Parse(File.ReadAllText(Path.Combine(callDir, "call.v1.json"))).RootElement;
            var documentId = call.GetProperty("documentId").GetString()!;
            var packId = call.GetProperty("packId").GetString()!;
            var atoms = AtomsFor(documentId);
            var byAlias = atoms.ToDictionary(a => a.Alias, StringComparer.Ordinal);
            var row = packRows[(documentId, packId)];
            var ownedAliases = row.GetProperty("ownedAliases").EnumerateArray().Select(a => a.GetString()!).ToArray();
            var visibleAliases = row.GetProperty("visibleAliases").EnumerateArray().Select(a => a.GetString()!).ToHashSet(StringComparer.Ordinal);
            var scope = ClaimBindingScope.Create(ownedAliases, visibleAliases);

            var content = File.ReadAllText(Path.Combine(callDir, "content.txt"));
            using var document = JsonDocument.Parse(content);
            var contract = DocxHeaderExtractor.DocumentProcessing.Projection.DocumentStructureTaskContract.Create();
            var response = SemanticClaimResponseCodecV2_1.Parse(document.RootElement, contract);

            var aggregateBinding = ExactClaimBinderV2_1.Bind(packId, response.Claims, atoms, scope);
            var packExactTextRefusals = aggregateBinding.Refusals.Values.Count(reason => V5BindingQualifier.RefusalFamily(reason) == V5BindingQualifier.FamilyExactTextBinding);
            claimLevelExactTextRefusals += packExactTextRefusals;

            int packClassB = 0, packClassC = 0, packZ = 0;

            for (var ordinal = 1; ordinal <= response.Claims.Count; ordinal++)
            {
                var proposal = response.Claims[ordinal - 1];
                foreach (var endpoint in new[] { proposal.Subject, proposal.Object })
                {
                    if (endpoint is null) continue;
                    foreach (var part in endpoint.SourceParts)
                    {
                        if (part.VerbatimText is null) continue; // alias-only already, nothing to counterfact.
                        var canonical = ProviderSourcePartNormalization.ToCanonical(part);
                        var partBinding = SemanticSourcePartBinder.Bind(atoms, [canonical]);
                        var atomText = byAlias[part.SourceAlias].Text;

                        if (partBinding.IsBound)
                        {
                            var bound = partBinding.Parts[0];
                            if (bound.Start != 0 || bound.End != atomText.Length) continue; // a genuine, necessary substring - never counterfacted away.
                            classATotal++;
                            var aliasOnly = SemanticSourcePartBinder.Bind(atoms, [new SemanticSourcePart(part.SourceAlias, CanonicalSemanticSelectionMode.WholeAlias)]);
                            if (aliasOnly.IsBound && aliasOnly.Parts[0].Start == 0 && aliasOnly.Parts[0].End == atomText.Length &&
                                aliasOnly.Parts[0].Alias == bound.Alias)
                                classAReplayedIdentical++;
                            continue;
                        }

                        var family = V5BindingQualifier.RefusalFamily(partBinding.Reason ?? partBinding.Status.ToString());
                        if (family != V5BindingQualifier.FamilyExactTextBinding) continue;
                        partLevelExactTextRefusals++;

                        var wholeSelection = ComputeWholeSelectionEquivalence(atomText, part.VerbatimText);
                        if (wholeSelection)
                        {
                            classBTotal++;
                            packClassB++;
                            var aliasOnly = SemanticSourcePartBinder.Bind(atoms, [new SemanticSourcePart(part.SourceAlias, CanonicalSemanticSelectionMode.WholeAlias)]);
                            if (!aliasOnly.IsBound) throw new InvalidOperationException("whole-selection-mechanical-match-must-bind-alias-only");
                            continue;
                        }

                        var diagnosis = V5ExactTextBindingAnalyzer.Analyze(
                            documentId, packId, ordinal, proposal.Predicate,
                            ReferenceEquals(endpoint, proposal.Subject) ? "subject" : "object", 0,
                            part.SourceAlias, part.VerbatimText, part.Occurrence, part.LeftExactContext, part.RightExactContext,
                            partBinding.Reason ?? partBinding.Status.ToString(), atoms, visibleAliases);

                        if (diagnosis.Category == V5ExactTextFailureCategory.MULTI_ATOM_OVERQUOTE)
                        {
                            classCTotal++;
                            packClassC++;
                            if (diagnosis.Counterfactuals.MultipartAliasSequenceWouldBind) classCVerified++;
                            continue;
                        }

                        packZ++;
                        zRows.Add(new { documentId, packId, sourceAlias = part.SourceAlias, category = diagnosis.Category.ToString() });
                    }
                }
            }

            packProfiles.Add(new PackCounterfactualProfile(documentId, packId, packClassB, packClassC, packZ, packExactTextRefusals));
        }

        return new CounterfactualWalkResult(
            claimLevelExactTextRefusals, partLevelExactTextRefusals,
            classATotal, classAReplayedIdentical, classBTotal, classCTotal, classCVerified, zRows, packProfiles);
    }

    /// <summary>
    /// True only when the quote, under some mechanical transform, is EQUAL to the complete named
    /// atom - never when it is merely contained in it. A proper-substring mechanical match is a
    /// genuine substring selection, not a whole-atom one, and must never be treated as alias-only
    /// safe: dropping verbatimText there would silently widen the model's selection to the whole atom.
    /// </summary>
    private static bool ComputeWholeSelectionEquivalence(string atomText, string quote)
    {
        if (quote.Length == 0) return false;
        string[] Forms(string text) =>
        [
            text,
            text.Normalize(System.Text.NormalizationForm.FormC),
            text.Normalize(System.Text.NormalizationForm.FormKC),
            V5ExactTextBindingAnalyzer.CollapseWhitespace(text),
            V5ExactTextBindingAnalyzer.RemoveWhitespace(text),
            V5ExactTextBindingAnalyzer.NormalizePunctuation(text),
            text.ToUpperInvariant(),
        ];
        var atomForms = Forms(atomText);
        var quoteForms = Forms(quote);
        for (var i = 0; i < atomForms.Length; i++)
            if (string.Equals(atomForms[i], quoteForms[i], StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void WriteJson(string relativePath, object value)
    {
        var path = TestRepository.Path(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}
