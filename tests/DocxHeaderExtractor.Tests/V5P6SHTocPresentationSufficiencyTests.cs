using System.Security.Cryptography;
using System.Text.Json;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// Provider-free P6S-H audit of the reviewed SRC-095 Table-of-Contents false positives.
/// It does not infer a relation Gold.  It reports whether the frozen request issued an available
/// text-correspondence target and whether the actual model still chose HEADING.
/// </summary>
public sealed class V5P6SHTocPresentationSufficiencyTests
{
    private const string Root = "artifacts/v5-p6s-candidate-authority";
    private const string SnapshotRoot = "eval/a99-closed-loop/pdf-canonical-source-v1";
    private const string GoldRoot = "eval/a99-closed-loop/gold-current/documents";
    private sealed record Part(string Alias, int Start, int End);
    private sealed record Gold(int Ordinal, string Identity, IReadOnlyList<Part> Parts, string Text);
    private sealed record TocFp(string PackId, string CandidateId, string Identity, IReadOnlyList<Part> Parts);

    [Fact]
    public void P6SH_audits_presentation_and_correspondence_authority_for_all_reviewed_toc_false_positives()
    {
        var diagnosisPath = TestRepository.Path($"{Root}/p6sg-preprojection-diagnosis/strict-exact-residual-decomposition.v1.json");
        using var diagnosis = JsonDocument.Parse(File.ReadAllText(diagnosisPath));
        Assert.Equal(0, diagnosis.RootElement.GetProperty("execution").GetProperty("providerCalls").GetInt32());
        var tocs = diagnosis.RootElement.GetProperty("falsePositives").EnumerateArray()
            .Where(item => item.GetProperty("DocumentId").GetString() == "SRC-095" && item.GetProperty("reason").GetString() == "REVIEWED_TOC_NAVIGATION_NONHEADING")
            .Select(item => new TocFp(item.GetProperty("PackId").GetString()!, item.GetProperty("CandidateId").GetString()!,
                item.GetProperty("Identity").GetString()!, Parts(item.GetProperty("parts")))).ToArray();
        Assert.Equal(87, tocs.Length);

        var sourceSha = CanonicalSemanticSourceHash.Compute(TestRepository.Path(SourcePdfCorpus.Src095));
        var plan = PdfCandidateAuthorityQualificationAdapter.PrepareFromSnapshot(TestRepository.Path($"{SnapshotRoot}/{sourceSha}.json"), "SRC-095");
        var atoms = plan.SourceAtoms.ToDictionary(atom => atom.Alias, StringComparer.Ordinal);
        var ordinalByAlias = plan.SourceAtoms.ToDictionary(atom => atom.Alias, atom => atom.Ordinal, StringComparer.Ordinal);
        var gold = ReadGold(atoms);
        var rows = new List<object>();

        foreach (var toc in tocs.OrderBy(item => item.PackId, StringComparer.Ordinal).ThenBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            var pack = plan.Packs.Single(item => item.PackId == toc.PackId);
            var candidate = pack.Universe.Candidates.Single(item => item.Id == toc.CandidateId);
            Assert.Equal(toc.Identity, Identity(candidate.Endpoint.Parts));
            var all = V5CandidateUniverseV1.Build(pack.OwnedAliases.Select(alias => atoms[alias]).ToArray(), plan.SourceAtoms,
                V5CandidatePolicyV1.Default with { MaxRelationsPerCandidate = int.MaxValue });
            var allForCandidate = all.Relations.Where(item => item.CandidateId == candidate.Id).ToArray();
            var issued = pack.Universe.Relations.Where(item => item.CandidateId == candidate.Id).ToArray();
            var goldCounterparts = gold.Where(item => item.Identity != toc.Identity &&
                    V5CandidateUniverseV1.Normalize(item.Text) == V5CandidateUniverseV1.Normalize(candidate.Text))
                .OrderBy(item => item.Ordinal).ToArray();
            var counterpartsByIdentity = goldCounterparts.ToDictionary(item => item.Identity, StringComparer.Ordinal);
            var matchingAll = allForCandidate.Where(item => counterpartsByIdentity.ContainsKey(item.TargetSpanIdentity)).ToArray();
            var matchingIssued = issued.Where(item => counterpartsByIdentity.ContainsKey(item.TargetSpanIdentity)).ToArray();
            var navSignal = HasNavigationSignal(pack.Request.UserMessage);
            var relationStatus = matchingAll.Length == 0 ? "NO_CORRESPONDENCE_RELATION" :
                matchingIssued.Length == 0 ? "RELATION_TARGET_TRUNCATED" :
                matchingIssued.Length > 1 ? "AMBIGUOUS_MULTI_TARGET" : "RELATION_TARGET_ISSUED";
            var presentationStatus = relationStatus != "RELATION_TARGET_ISSUED" ? "NOT_EVALUABLE_FROM_SINGLE_ISSUED_TARGET" :
                !navSignal ? "PRESENTATION_CONTEXT_WEAK" : "PRESENTATION_SUFFICIENT_BUT_SEMANTIC_ERROR";
            rows.Add(new
            {
                toc.PackId, toc.CandidateId, toc.Identity, candidateText = candidate.Text, candidateExtentKind = candidate.Kind.ToString(), candidatePage = candidate.Page,
                overlappingCandidateVariants = pack.Universe.Candidates.Count(item => Overlap(
                    item.Endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray(),
                    candidate.Endpoint.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray())),
                modelDecision = "HEADING", relationStatus, presentationStatus, navigationSignalInFrozenRequest = navSignal,
                goldBodyCounterparts = goldCounterparts.Select(item => new { item.Ordinal, item.Identity, item.Text }).ToArray(),
                issuedRelations = issued.Select(item => new { item.Id, item.TargetSpanIdentity, item.TargetText, item.TargetPage, item.MatchTier,
                    rankBeforeCap = Rank(allForCandidate, candidate, ordinalByAlias, item.TargetSpanIdentity), targetIsGoldCounterpart = counterpartsByIdentity.ContainsKey(item.TargetSpanIdentity) }).ToArray(),
                truncatedGoldCounterparts = matchingAll.Where(item => !issued.Any(issuedRelation => issuedRelation.TargetSpanIdentity == item.TargetSpanIdentity))
                    .Select(item => new { item.TargetSpanIdentity, item.TargetText, item.TargetPage, item.MatchTier, rankBeforeCap = Rank(allForCandidate, candidate, ordinalByAlias, item.TargetSpanIdentity) }).ToArray(),
                requestContext = ContextExcerpt(pack.Request.UserMessage),
                modelVisibleSourceOrderWindow = SourceOrderWindow(plan, pack, candidate.Endpoint.Parts[0].Alias),
            });
        }

        // The response did not merely mark these entries as representations: 86 have no
        // representation overlap and none has an exact representation candidate.
        var capture = ReadCapture(plan);
        var representationOverlap = tocs.Count(toc => capture.Representations.Any(item => Overlap(item.Parts, toc.Parts)));
        var representationExact = tocs.Count(toc => capture.Representations.Any(item => item.Identity == toc.Identity));
        Assert.Equal(1, representationOverlap); Assert.Equal(0, representationExact);

        var output = new
        {
            schemaVersion = "v5-p6sh-toc-presentation-sufficiency-v1",
            execution = new { providerCalls = 0, goldRead = true, goldMutation = "NONE", runtimeChanged = false,
                diagnosisSource = "artifacts/v5-p6s-candidate-authority/p6sg-preprojection-diagnosis/strict-exact-residual-decomposition.v1.json",
                diagnosisSourceSha256 = Hash(File.ReadAllBytes(diagnosisPath)) },
            authority = new
            {
                scope = "Reviewed SRC-095 CONTENTS_ENTRY false positives only. Relation targets are diagnostic read-only evidence; relation Gold is absent.",
                relationStatus = "Issued/truncated/no-correspondence is measured only against same-normalized-text canonical heading occurrences, not asserted as a relation truth.",
                presentationStatus = "A sufficient result means exactly one body-heading counterpart target was issued and a Table of Contents signal was model-visible in that frozen request; it does not claim causal proof beyond the observed HEADING decision.",
            },
            totals = new
            {
                reviewedTocHeadingFalsePositives = tocs.Length,
                representationDecisionsSrc095 = capture.Representations.Count,
                tocWithAnyRepresentationOverlap = representationOverlap,
                tocWithExactRepresentation = representationExact,
                relationStatuses = rows.GroupBy(row => (string)row.GetType().GetProperty("relationStatus")!.GetValue(row)!, StringComparer.Ordinal).OrderBy(item => item.Key).Select(item => new { status = item.Key, count = item.Count() }).ToArray(),
                presentationStatuses = rows.GroupBy(row => (string)row.GetType().GetProperty("presentationStatus")!.GetValue(row)!, StringComparer.Ordinal).OrderBy(item => item.Key).Select(item => new { status = item.Key, count = item.Count() }).ToArray(),
            },
            rows,
        };
        FreezeArtifact.AssertJson($"{Root}/p6sh-toc-presentation-sufficiency", "reviewed-toc-hard-negative-audit.v1.json", output);
    }

    private static (List<Prediction> Headings, List<Prediction> Representations) ReadCapture(PdfCandidateAuthorityDocumentPlan plan)
    {
        using var capture = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{Root}/p6sd-full31/result.v1.json")));
        var headings = new List<Prediction>(); var representations = new List<Prediction>();
        foreach (var row in capture.RootElement.GetProperty("rows").EnumerateArray().Where(item => item.GetProperty("documentId").GetString() == "SRC-095"))
        {
            var pack = plan.Packs.Single(item => item.PackId == row.GetProperty("PackId").GetString());
            var raw = row.GetProperty("rawResponse").GetString()!;
            Assert.Equal(Hash(System.Text.Encoding.UTF8.GetBytes(raw)), row.GetProperty("rawResponseSha256").GetString());
            var parsed = PdfCandidateAuthorityQualificationAdapter.ParseCandidateDecision(pack, raw);
            foreach (var decision in parsed.AcceptedBeforeOverlapQuarantine)
            {
                var parts = decision.Candidate.Endpoint.Parts.Select(item => new Part(item.Alias, item.Start, item.End)).ToArray();
                var prediction = new Prediction(pack.PackId, decision.Candidate.Id, Identity(parts), parts);
                if (decision.Kind == V5CandidateDecisionKind.HEADING) headings.Add(prediction); else representations.Add(prediction);
            }
        }
        return (headings, representations);
    }

    private static List<Gold> ReadGold(IReadOnlyDictionary<string, SemanticSourceAtom> atoms)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(TestRepository.Path($"{GoldRoot}/SRC-095.gold.v1.json"))); var ordinal = 0;
        return json.RootElement.GetProperty("semantic").GetProperty("claims").EnumerateArray().Select(claim =>
        {
            var sourceParts = JsonSerializer.Deserialize<List<SemanticSourcePart>>(claim.GetProperty("sourceParts").GetRawText())!;
            var binding = SemanticSourcePartBinder.Bind(atoms.Values.OrderBy(atom => atom.Ordinal).ToArray(), sourceParts);
            Assert.True(binding.IsBound, binding.Reason);
            var parts = binding.Parts.Select(part => new Part(part.Alias, part.Start, part.End)).ToArray();
            var text = string.Join(" ", parts.Select(part => atoms[part.Alias].Text[part.Start..part.End]));
            return new Gold(++ordinal, Identity(parts), parts, text);
        }).ToList();
    }

    private static bool HasNavigationSignal(string userMessage)
    {
        using var request = JsonDocument.Parse(userMessage);
        return request.RootElement.GetProperty("candidates").EnumerateArray().Select(item => item.GetProperty("text").GetString()!)
            .Concat(request.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray().Select(item => item.GetProperty("text").GetString()!))
            .Any(text => V5CandidateUniverseV1.Normalize(text).Contains("TABLE OF CONTENTS", StringComparison.Ordinal));
    }

    private static object ContextExcerpt(string userMessage)
    {
        using var request = JsonDocument.Parse(userMessage);
        var context = request.RootElement.GetProperty("contextOnlyEvidence").EnumerateArray().Select(item => new { page = item.GetProperty("page").GetInt32(), text = item.GetProperty("text").GetString()! }).ToArray();
        return new { contextOnlyCount = context.Length, tableOfContentsSignal = context.Where(item => V5CandidateUniverseV1.Normalize(item.text).Contains("TABLE OF CONTENTS", StringComparison.Ordinal)).ToArray() };
    }

    private static object SourceOrderWindow(PdfCandidateAuthorityDocumentPlan plan, PdfCandidateAuthorityPreparedPack pack, string primaryAlias)
    {
        var visible = pack.VisibleAliases.ToHashSet(StringComparer.Ordinal);
        var selectable = pack.OwnedAliases.ToHashSet(StringComparer.Ordinal);
        var ordered = plan.SourceAtoms.OrderBy(item => item.Ordinal).ToArray();
        var index = Array.FindIndex(ordered, item => item.Alias == primaryAlias);
        Assert.True(index >= 0);
        object Row(SemanticSourceAtom atom) => new { atom.Alias, atom.Page, atom.Ordinal, atom.Text, selectable = selectable.Contains(atom.Alias), contextOnly = visible.Contains(atom.Alias) && !selectable.Contains(atom.Alias) };
        return new
        {
            primaryAlias,
            before = ordered.Take(index).Reverse().Where(item => visible.Contains(item.Alias)).Take(2).Reverse().Select(Row).ToArray(),
            after = ordered.Skip(index + 1).Where(item => visible.Contains(item.Alias)).Take(2).Select(Row).ToArray(),
        };
    }

    private static int Rank(IReadOnlyList<V5IssuedRelationV1> rows, V5IssuedCandidateV1 candidate,
        IReadOnlyDictionary<string, int> ordinalByAlias, string identity)
    {
        var primaryOrdinal = ordinalByAlias[candidate.Endpoint.Parts[0].Alias];
        return rows.OrderBy(item => Math.Abs(ordinalByAlias[TargetPrimaryAlias(item.TargetSpanIdentity)] - primaryOrdinal))
            .ThenBy(item => item.TargetSpanIdentity, StringComparer.Ordinal).Select((item, index) => new { item, rank = index + 1 })
            .Single(item => item.item.TargetSpanIdentity == identity).rank;
    }

    private static string TargetPrimaryAlias(string identity)
    {
        var first = identity.Split('|')[0];
        return first[..first.LastIndexOf(':')];
    }
    private static IReadOnlyList<Part> Parts(JsonElement array) => array.EnumerateArray().Select(item => new Part(item.GetProperty("Alias").GetString()!, item.GetProperty("Start").GetInt32(), item.GetProperty("End").GetInt32())).ToArray();
    private static string Identity(IReadOnlyList<BoundSourcePart> parts) => string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static string Identity(IReadOnlyList<Part> parts) => string.Join("|", parts.Select(part => $"{part.Alias}:{part.Start}-{part.End}"));
    private static bool Overlap(IReadOnlyList<Part> left, IReadOnlyList<Part> right) => left.Any(a => right.Any(b => a.Alias == b.Alias && Math.Max(a.Start, b.Start) < Math.Min(a.End, b.End)));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record Prediction(string PackId, string CandidateId, string Identity, IReadOnlyList<Part> Parts);
}
