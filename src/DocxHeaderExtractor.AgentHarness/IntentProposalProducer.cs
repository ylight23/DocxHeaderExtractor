using DocxHeaderExtractor.Application.Tasks;

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
        return new(
            "extract-document-structure",
            ["document-structure"],
            [],
            "document",
            null,
            "outline",
            [],
            request.WantsAction);
    }
}
