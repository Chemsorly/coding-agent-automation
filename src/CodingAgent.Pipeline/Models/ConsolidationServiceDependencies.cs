using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Groups the constructor dependencies of <see cref="Services.ConsolidationService"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// <see cref="PipelineConfigStore"/> supplies the live global configuration at trigger time; without
/// it (tests), <see cref="Config"/> stands in for it.
/// </summary>
public sealed record ConsolidationServiceDependencies(
    Serilog.ILogger Logger,
    PipelineConfiguration Config,
    IProjectStore ProjectStore,
    IPipelineRunHistoryService RunHistoryService,
    IConsolidationRunStore RunStore,
    IHarnessSuggestionStore HarnessSuggestionStore,
    IProviderConfigStore ProviderConfigStore,
    IConsolidationFeedbackCache? FeedbackCache = null,
    IWorkDistributor? WorkDistributor = null,
    IConsolidationSelectorResolver? SelectorResolver = null,
    IPipelineConfigStore? PipelineConfigStore = null);
