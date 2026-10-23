namespace DocxHeaderExtractor.Contracts.Provider;

/// <summary>What an agent needs to call the model in one run: the provider key of the model route, and a check that throws once the
/// lane that started the run has ended (its result would no longer be used). Every problem's agents receive it the same way.</summary>
public sealed record ModelAccess(string ApiKey, Action EnsureActive);
