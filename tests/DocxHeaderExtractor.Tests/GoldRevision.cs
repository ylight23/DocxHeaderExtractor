using System.Text.Json;
using System.Text.Json.Nodes;
using DocxHeaderExtractor.Core.Models;
using DocxHeaderExtractor.DocumentProcessing.Pipeline;
using DocxHeaderExtractor.Tests.GenericAudit.V1_1;

namespace DocxHeaderExtractor.Tests;

/// <summary>
/// The mechanics of a user-approved Gold revision that adds occurrences: each added claim is a Gold sibling's claim
/// (its axes) with the new occurrence's parts, bound by the production binder, and the claims stay in source order.
/// The decision itself is the user's and the revision record's; nothing here chooses what is a heading.
/// </summary>
internal static class GoldRevision
{
    /// <summary>One added occurrence: the whole atom, or the verbatim text the user approved inside it.</summary>
    internal sealed record Addition(string Alias, string? VerbatimText, string Pattern, string Evidence);

    internal static string First(JsonNode claim) => claim["sourceParts"]![0]!["sourceAlias"]!.GetValue<string>();

    internal static JsonNode Add(JsonNode gold, string pdf, string template, IReadOnlyList<Addition> additions)
    {
        var atoms = PdfSourceOccurrenceAdapter.Build(TestRepository.Path(pdf)).Atoms;
        var claims = gold["occurrence"]!["claims"]!.AsArray().Select(c => c!.DeepClone()).ToList();
        var model = claims.Single(c => First(c) == template);
        foreach (var addition in additions)
        {
            Assert.DoesNotContain(claims, c => c["boundParts"]!.AsArray().Any(p => p!["sourceAlias"]!.GetValue<string>() == addition.Alias));
            var part = addition.VerbatimText is null
                ? new SemanticSourcePart(addition.Alias, CanonicalSemanticSelectionMode.WholeAlias)
                : new SemanticSourcePart(addition.Alias, CanonicalSemanticSelectionMode.VerbatimText, addition.VerbatimText);
            var binding = SemanticSourcePartBinder.Bind(atoms, new SemanticSourcePartsProposal([part]));
            Assert.True(binding.IsBound, binding.Reason);
            var bound = Assert.Single(binding.Parts);
            var atom = atoms.Single(a => a.Alias == addition.Alias);

            var claim = model.DeepClone();
            claim["approvedWording"] = bound.Text;
            claim["sourceParts"] = new JsonArray(new JsonObject
            {
                ["sourceAlias"] = addition.Alias,
                ["selectionMode"] = part.SelectionMode,
                ["verbatimText"] = part.VerbatimText,
                ["occurrence"] = null,
                ["leftExactContext"] = null,
                ["rightExactContext"] = null,
            });
            claim["identity"] = binding.Identity;
            claim["projectedText"] = bound.Text;
            claim["boundParts"] = new JsonArray(new JsonObject
            {
                ["sourceAlias"] = addition.Alias,
                ["page"] = atom.Page,
                ["row"] = atom.Row,
                ["utf16Span"] = new JsonObject { ["start"] = bound.Start, ["end"] = bound.End },
                ["text"] = bound.Text,
                ["localityFromPrevious"] = null,
            });
            claim["pattern"] = addition.Pattern;
            claim["evidence"] = addition.Evidence;
            claims.Add(claim);
        }
        var ordinal = atoms.ToDictionary(a => a.Alias, a => a.Ordinal, StringComparer.Ordinal);
        var ordered = claims.OrderBy(c => ordinal[c["boundParts"]![0]!["sourceAlias"]!.GetValue<string>()])
            .ThenBy(c => c["boundParts"]![0]!["utf16Span"]!["start"]!.GetValue<int>()).ToArray();
        gold["occurrence"]!["claims"] = new JsonArray(ordered);
        gold["semanticHeadingTotal"] = ordered.Length;
        return gold;
    }

    /// <summary>The committed blind proposals of a revealed held-out against a revised Gold: a diagnostic, never a held-out score.</summary>
    internal static object Diagnostic(JsonNode gold, string pdf, string proposals, string status)
    {
        var path = Path.Combine(Path.GetTempPath(), $"a99-gold-revision-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, gold.ToJsonString(FreezeArtifact.Json));
            var universe = ExactScorer.Universe.For("PDF", TestRepository.Path(pdf));
            var score = ExactScorer.Compute(ExactScorer.ReadGold(path, universe), ExactScorer.ReadProposals(TestRepository.Path(proposals), universe));
            using var headline = JsonDocument.Parse(JsonSerializer.Serialize(score.Headline(), FreezeArtifact.Json));
            var h = headline.RootElement;
            return new
            {
                status,
                scorer = ExactScorer.ScorerId,
                goldClaims = h.GetProperty("goldClaims").GetInt32(),
                truePositives = h.GetProperty("truePositives").GetInt32(),
                falsePositives = h.GetProperty("falsePositives").GetInt32(),
                falseNegatives = h.GetProperty("falseNegatives").GetInt32(),
                precision = h.GetProperty("truePrecision").GetDouble(),
                recall = h.GetProperty("trueRecall").GetDouble(),
                f1 = h.GetProperty("f1").GetDouble(),
            };
        }
        finally
        {
            File.Delete(path);
        }
    }
}
