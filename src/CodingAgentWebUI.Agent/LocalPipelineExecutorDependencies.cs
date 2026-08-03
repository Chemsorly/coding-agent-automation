using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="LocalPipelineExecutor"/> to reduce
/// constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record LocalPipelineExecutorDependencies(
    IKiroCliOrchestrator Orchestrator,
    System.Net.Http.IHttpClientFactory HttpClientFactory,
    PipelineConfiguration DefaultPipelineConfig,
    IQualityGateValidator QualityGateValidator,
    Serilog.ILogger Logger,
    IBrainUpdateService? BrainUpdateService = null,
    IPipelineRunHistoryService? HistoryService = null,
    IOpenIssueContextWriter? OpenIssueContextWriter = null,
    AgentId? AgentIdentity = null,
    IPipelineReporterFactory? ReporterFactory = null);
