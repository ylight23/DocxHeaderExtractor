using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Contracts.Problems;

/// <summary>
/// The AI half of a problem: reads the source and proposes an answer. It decides nothing final; <see cref="IChecker{TProposal, TOutput}"/>
/// keeps only what the source supports.
/// </summary>
/// <typeparam name="TProposal">What the agent proposes, in the problem's own shape (entities, table cells, review notes).</typeparam>
public interface IProposer<TProposal> where TProposal : class
{
    /// <summary>The protocol the agent speaks (prompt and contract version), recorded with every run.</summary>
    string Contract { get; }

    /// <summary>Proposes an answer for <paramref name="source"/>. Every model call goes through <paramref name="access"/>, whose
    /// EnsureActive throws once the run has ended.</summary>
    Task<TProposal> ProposeAsync(ProblemSource source, ModelAccess access, CancellationToken ct);
}
