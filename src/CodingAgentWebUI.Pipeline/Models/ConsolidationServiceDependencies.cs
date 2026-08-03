using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the constructor dependencies of <see cref="Services.ConsolidationService"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record ConsolidationServiceDependencies(
    Serilog.ILogger Logger,
    PipelineConfiguration Config,
    IProjectStore ProjectStore,
    IPipelineRunHistoryService RunHistoryService,
    IConsolidationRunStore RunStore,
    IHarnessSuggestionStore HarnessSuggestionStore,
    IConsolidationDispatchService? Dispatcher = null,
    IConsolidationWorkspaceManager? WorkspaceManager = null,
    IConsolidationFeedbackCache? FeedbackCache = null);
