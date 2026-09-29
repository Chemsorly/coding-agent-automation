using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Agent;

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
    AgentId? AgentIdentity = null,
    IPipelineReporterFactory? ReporterFactory = null,
    /// <summary>
    /// When non-null, overrides the internal <see cref="AgentProviderFactory"/> during provider
    /// resolution. Use in tests to substitute fake repository and agent providers without
    /// needing a real Kiro CLI, git remote, or cloud credentials.
    /// </summary>
    IProviderFactory? ProviderFactoryOverride = null);
