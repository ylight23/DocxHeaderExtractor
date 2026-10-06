using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.Core.V5;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;

namespace DocxHeaderExtractor.Tests;

public sealed class PdfReadOnlyCorrespondenceBuilderTests
{
    [Fact]
    public void Pdf_read_only_builder_matches_frozen_candidate_universe_projection()
    {
        var document = new[]
        {
            Atom("A1", 0, 1, "1. Scope ofHTTP"),
            Atom("A2", 1, 1, " and applicability"),
            Atom("A3", 2, 1, "ordinary body"),
            Atom("A4", 3, 2, "1. Scope of HTTP and applicability"),
            Atom("A5", 4, 2, "TABLE OF CONTENTS"),
            Atom("A6", 5, 2, "1. Scope of HTTP"),
            Atom("A7", 6, 2, "and applicability"),
            Atom("A8", 7, 2, "HTTP"),
            Atom("A9", 8, 2, "3.7"),
            Atom("A10", 9, 2, " "),
        };
        var owned = document.Take(3).ToArray();

        var expected = FromFrozenCandidateUniverse(owned, document);
        var actual = PdfReadOnlyCorrespondenceBuilder.Build(owned, document);

        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.Ordinal), actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var alias in expected.Keys)
            Assert.Equal(expected[alias], actual[alias]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>> FromFrozenCandidateUniverse(
        IReadOnlyList<SemanticSourceAtom> owned, IReadOnlyList<SemanticSourceAtom> all)
    {
        var universe = V5CandidateUniverseV1.Build(owned, all, V5CandidatePolicyV1.Default);
        var candidates = universe.Candidates.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<V5ReadOnlyCorrespondenceV1>>(StringComparer.Ordinal);
        foreach (var relation in universe.Relations)
        {
            if (!candidates.TryGetValue(relation.CandidateId, out var candidate)) continue;
            var alias = candidate.Endpoint.Parts[0].Alias;
            if (!owned.Any(atom => atom.Alias == alias)) continue;
            var list = result.TryGetValue(alias, out var old) ? old.ToList() : [];
            if (!list.Any(value => value.TargetPage == relation.TargetPage && value.TargetText == relation.TargetText))
                list.Add(new V5ReadOnlyCorrespondenceV1(relation.TargetPage, relation.TargetText));
            result[alias] = list;
        }
        return result;
    }

    private static SemanticSourceAtom Atom(string alias, int ordinal, int page, string text) =>
        new(alias, $"source-{alias}", ordinal, page, 1, 0, text);
}
