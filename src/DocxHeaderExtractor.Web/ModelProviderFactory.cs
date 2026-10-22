using DocxHeaderExtractor.Contracts.Provider;

namespace DocxHeaderExtractor.Web;

/// <summary>The model provider of one request. Production reads the OpenRouter key from the environment; a test registers its own.</summary>
public delegate IModelProvider ModelProviderFactory();
