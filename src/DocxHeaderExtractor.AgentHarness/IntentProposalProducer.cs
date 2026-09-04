using DocxHeaderExtractor.Application.Tasks;
using System.Text.RegularExpressions;

namespace DocxHeaderExtractor.AgentHarness;

/// <summary>
/// Outer-host seam for conversational intent production. Implementations may be backed by a UI,
/// framework, or another trusted producer, but the returned proposal is always validated by the
/// harness before a plan or capability is created.
/// </summary>
public interface IIntentProposalProducer
{
    IntentProposal Propose(DocumentAgentRequest request);
}

/// <summary>
/// Default adapter for the current document workflow. It preserves the existing structured
/// request contract while allowing a conversational/framework producer to be injected later.
/// </summary>
public sealed class DocumentIntentProposalProducer : IIntentProposalProducer
{
    public IntentProposal Propose(DocumentAgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = string.IsNullOrWhiteSpace(request.UserPrompt)
            ? "Extract the document structure."
            : request.UserPrompt.Trim();
        var normalized = prompt.ToLowerInvariant();
        var structureRequested = ContainsAny(normalized,
            "structure", "outline", "heading", "hierarchy", "tree",
            "cấu trúc", "tiêu đề", "cây");
        var unsupportedOperation = ContainsAny(normalized,
            "translate", "summarize", "summary", "classify", "rewrite", "delete", "convert");

        if (unsupportedOperation)
        {
            return new(
                "unsupported-document-operation",
                [],
                [],
                "document",
                null,
                "outline",
                [prompt],
                request.WantsAction);
        }

        if (!structureRequested)
        {
            return new(
                "",
                [],
                [],
                "",
                null,
                "",
                [prompt],
                request.WantsAction);
        }

        var depth = ParseStructuralDepth(normalized);
        var outputShape = "outline";

        return new(
            "extract-document-structure",
            ["document-structure"],
            ["requested-structure"],
            "document",
            depth,
            outputShape,
            [$"user-goal:{prompt}"],
            request.WantsAction);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(value.Contains);

    private static int? ParseStructuralDepth(string prompt)
    {
        var match = Regex.Match(prompt,
            @"\b(?:to|up\s+to|through|at\s+most)\s+(?<depth>-?\d+|one|two|three|four|five|six|seven|eight|nine)\s+levels?\b",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        return match.Groups["depth"].Value switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            var numeric => int.Parse(numeric, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
