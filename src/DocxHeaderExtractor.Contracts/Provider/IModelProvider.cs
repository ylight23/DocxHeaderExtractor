namespace DocxHeaderExtractor.Contracts.Provider;

/// <summary>
/// What a host gives the pipeline for the model: the route its agents call, and any agent of its own. App/OpenRouterProvider is the production
/// one. A host supplies an agent by answering <see cref="Create{T}"/> for its role name (see Contracts/Agents/AgentRoles); a new problem adds
/// a role name and never a new member here.
/// </summary>
public interface IModelProvider
{
    /// <summary>The model route of a run that calls the model. A provider that has none refuses; there is no other route to fall back to.</summary>
    ModelRoute CreateRoute() => throw new InvalidOperationException("MODEL_ROUTE_REQUIRED");

    /// <summary>
    /// The agent this host supplies for <paramref name="role"/>, or null, the default, which makes the composition root use the production one.
    /// <typeparamref name="T"/> is the interface the role is filled with.
    /// </summary>
    T? Create<T>(string role) where T : class => null;
}
