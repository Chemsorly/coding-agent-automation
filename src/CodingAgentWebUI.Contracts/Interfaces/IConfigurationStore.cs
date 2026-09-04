namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IConfigurationStore : IPipelineConfigStore, IProviderConfigStore,
    IAgentProfileStore, IQualityGateConfigStore, IReviewerConfigStore, IProjectStore
{
    /// <summary>
    /// Clears all internal caches, forcing the next read to go to the backing store.
    /// Call after external writes that bypass the store (e.g., bulk import).
    /// </summary>
    void InvalidateCaches() { }
}
