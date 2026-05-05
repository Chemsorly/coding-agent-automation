namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IConfigurationStore : IPipelineConfigStore, IProviderConfigStore,
    IAgentProfileStore, IQualityGateConfigStore, IReviewerConfigStore { }
