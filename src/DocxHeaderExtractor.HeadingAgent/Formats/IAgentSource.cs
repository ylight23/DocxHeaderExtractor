using DocxHeaderExtractor.Contracts.Agents;
using DocxHeaderExtractor.HeadingAgent.Grouping;
using DocxHeaderExtractor.HeadingAgent.Protocol;
using DocxHeaderExtractor.HeadingAgent.Tools;

namespace DocxHeaderExtractor.HeadingAgent.Formats;

/// <summary>
/// The document an agent run reads: its lines in reading order under the aliases of the route's source snapshot, the identity hashes, the
/// segments (the targets decided per conversation and the context shown with them) and the flavor that says how this kind of document is
/// presented to the model. A PDF source and a DOCX source implement it; the runner, the grouping step and the selector depend only on this.
/// </summary>
internal interface IAgentSource
{
    string SourceSha256 { get; }
    string SourceAliasUniverseSha256 { get; }
    string EvidenceStoreSha256 { get; }
    /// <summary>The lines (PDF) or paragraphs (DOCX) in reading order. A DOCX paragraph has no page, bbox or typography.</summary>
    IReadOnlyList<AgentSourceLine> Lines { get; }
    /// <summary>The decided range of each segment, [Start, End) into <see cref="Lines"/>, covering every line once.</summary>
    IReadOnlyList<(int Start, int End)> Cores { get; }
    /// <summary>The targets of one segment and the lines shown with them, as line indices.</summary>
    (string[] Decide, int[] Shown) Scope(int segment);
    IAgentFormat Flavor { get; }
}
