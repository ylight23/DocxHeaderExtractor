using DocxHeaderExtractor.Contracts.Agents;

namespace DocxHeaderExtractor.Contracts.Provider;

/// <summary>
/// What a host gives the pipeline for the model: the route its agents call, and any agent of its own. App/OpenRouterProvider is the production
/// one; a test or an evaluation run supplies its own agents through the Create hooks.
/// </summary>
public interface IModelProvider
{
    /// <summary>The model route of a run that calls the model. A provider that has none refuses; there is no other route to fall back to.</summary>
    ModelRoute CreateRoute() => throw new InvalidOperationException("MODEL_ROUTE_REQUIRED");

    /// <summary>The PDF heading selector, when the host supplies its own. Null, the default, makes the composition root use the production agent.</summary>
    IHeadingSelector? CreatePdfHeadingSelector() => null;

    /// <summary>The DOCX heading selector, as <see cref="CreatePdfHeadingSelector"/> for a PDF.</summary>
    IHeadingSelector? CreateDocxHeadingSelector() => null;

    /// <summary>The heading placer, when the host supplies its own. Null, the default, makes the composition root use the production placer.</summary>
    IHeadingPlacer? CreateHeadingPlacer() => null;

    /// <summary>The scan selector (OCR, transcription and selection), when the host supplies its own.</summary>
    IScanHeadingSelector? CreateScanHeadingSelector() => null;
}
