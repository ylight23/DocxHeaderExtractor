namespace DocxHeaderExtractor.DocumentProcessing.Inference;

/// <summary>Provider-neutral transport contract consumed by document processing.</summary>
public interface IInferenceTransport : IDisposable
{
    string ModelName { get; }
    int ContextSize { get; }
    string RuntimeDescription { get; }
    /// <summary>Số token prefix đã cache; backend không hỗ trợ trả 0.</summary>
    int SharedPrefixTokens { get; }

    /// <summary>
    /// The one model call the canonical lanes make: a system prompt and a user message, and the
    /// completion text back (trimmed). Callers validate the reply against their own contract;
    /// the backend guarantees nothing about its shape.
    /// </summary>
    /// <param name="expectedItemCount">
    /// How many result items the caller is asking about, when it knows. Backends that must size an
    /// output budget use it instead of guessing from the payload's shape. 0 means "unknown, infer".
    /// Guessing was a real defect: the budget was derived by counting a field name, so renaming a
    /// field in the request silently collapsed the budget to its floor and truncated the reply.
    /// </param>
    Task<string> BoundaryCutAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken ct = default,
        int expectedItemCount = 0);
}

/// <summary>
/// Composition-root seam for inference providers. DocumentProcessing consumes the neutral transport contract;
/// provider construction belongs to Infrastructure.
/// </summary>
public interface IInferenceTransportFactory
{
    bool SendsDataExternally => false;

    /// <summary>
    /// Creates the separately-authorized production transport for the agent routes (PDF and DOCX).
    /// Both routes call the provider through the V6 heading selector and need the key this transport carries.
    /// A factory that has not explicitly opted into the qualified agent route fails closed; there is no
    /// other (local or default) transport for a route to fall back to.
    /// </summary>
    Task<IInferenceTransport> CreateAgentRouteAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException("AGENT_ROUTE_PROVIDER_FACTORY_REQUIRED");

    /// <summary>
    /// The PDF heading selector of this provider route, when the factory supplies its own (a host or a test that drives the selection
    /// itself). Null, the default, makes the composition root use the production V6 agent.
    /// </summary>
    DocxHeaderExtractor.DocumentProcessing.Routing.IHeadingSelector? CreatePdfHeadingSelector() => null;

    /// <summary>The DOCX heading selector of this provider route, as <see cref="CreatePdfHeadingSelector"/> for a PDF. Null, the default, makes the
    /// composition root use the production DOCX agent.</summary>
    DocxHeaderExtractor.DocumentProcessing.Routing.IHeadingSelector? CreateDocxHeadingSelector() => null;
}

/// <summary>
/// Marker issued only by a composition root that has authorized the qualified agent route (PDF or DOCX). It names the
/// provider and model the route was qualified on and carries the provider key: ordinary production must
/// not borrow another transport merely to reach the PDF agent.
/// </summary>
public interface IAgentRouteInferenceTransport : IInferenceTransport
{
    string RouteProvider { get; }
    string RouteModel { get; }

    /// <summary>The provider key of the authorized route, for the heading selector, which calls the provider itself (V6 promotion).
    /// Null when the composition root issued none; the lane then refuses before any call.</summary>
    string? OpenRouterApiKey => null;
}
