using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="PipelineExecutionContextBuilder"/> to reduce
/// constructor parameter count (S107). Optional members default to null.
/// </summary>
internal sealed record PipelineExecutionContextBuilderDependencies(
    IQualityGateValidator QualityGateValidator,
    IPipelineReporterFactory ReporterFactory,
    FeedbackService FeedbackService,
    AgentId AgentId,
    Serilog.ILogger Logger,
    IBrainUpdateService? BrainUpdateService = null,
    IPipelineRunHistoryService? HistoryService = null,
    PullRequestFinalizationService? Finalization = null);
