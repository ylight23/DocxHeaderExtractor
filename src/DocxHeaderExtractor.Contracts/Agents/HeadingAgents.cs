namespace DocxHeaderExtractor.Contracts.Agents;

/// <summary>
/// The agents the heading lanes run with: a selector per kind of source, which says which lines are headings, and the placer, which
/// proposes where each heading sits. The pipeline only checks what they return.
/// <para>
/// The composition root fills this (App/HeadingAgentSetup); a host or a test may pass its own agents. To add an agent to the heading
/// lanes: give it an interface in this folder, a property here, a Create hook on IInferenceTransportFactory, and its default in
/// HeadingAgentSetup. Another problem (tables, review) gets its own record like this one.
/// </para>
/// </summary>
public sealed record HeadingAgents(
    IHeadingSelector? Pdf = null,
    IHeadingSelector? Docx = null,
    IScanHeadingSelector? Scan = null,
    IHeadingPlacer? Placer = null);
