namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// The configuration stores in one. Agent profiles are not part of it (spec 053 req 4.2, 10.3): only the API reads them,
/// through <see cref="IAgentProfileStore"/>, which its database store also implements.
/// </summary>
public interface IConfigurationStore : IPipelineConfigStore, IProviderConfigStore,
    IQualityGateConfigStore, IReviewerConfigStore, IProjectStore
{
    /// <summary>
    /// Clears all internal caches, forcing the next read to go to the backing store.
    /// Call after external writes that bypass the store (e.g., bulk import).
    /// </summary>
    void InvalidateCaches() { }
}
