namespace DocxHeaderExtractor.Contracts.Provider;

/// <summary>
/// The model a run is allowed to call and the provider key it calls it with. Only an <see cref="IModelProvider"/> issues one; with the model
/// disabled a run has none, and a lane that needs the model refuses without it. The agents send their own requests with the key.
/// </summary>
public sealed record ModelRoute(string Model, string? ApiKey)
{
    /// <summary>Names the model only: the key never reaches a log or an exception message through a route.</summary>
    public override string ToString() => Model;
}
