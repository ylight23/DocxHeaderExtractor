namespace DocxHeaderExtractor.Entities;

/// <summary>What the agent proposes: the entities it found, and the batches it could not get an answer for (with the reason).</summary>
public sealed record EntityProposal(IReadOnlyList<ProposedEntity> Entities, IReadOnlyList<string> BatchFailures);

/// <summary>One entity as the model proposed it: a source unit, the span of its text (end exclusive) and a type.</summary>
public sealed record ProposedEntity(string SourceId, int Start, int End, string Type);

/// <summary>An entity the checker kept: its span is inside the unit's text, its type is one of <see cref="EntityTypes.All"/>.</summary>
public sealed record EntityFound(string SourceId, int Start, int End, string Type, string Text);

/// <summary>A proposal the checker refused, with why.</summary>
public sealed record EntityRefusal(ProposedEntity Entity, string Reason);

/// <summary>The problem's output: the entities kept, the proposals refused, and the batches that failed.</summary>
public sealed record EntityOutput(IReadOnlyList<EntityFound> Entities, IReadOnlyList<EntityRefusal> Refused, IReadOnlyList<string> BatchFailures);
