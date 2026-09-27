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
    // TODO [WARNING]: ProviderFactoryOverride is appended after ReporterFactory in this positional
    // record. Any internal caller that constructs LocalPipelineExecutorDependencies using positional
    // syntax (rather than named arguments) will silently pass its ReporterFactory value to this
    // parameter without a compile error. Verify that no internal callers use positional init syntax
    // before assuming this is safe. (LocalPipelineExecutorDependencies is internal and the record
    // is not used outside CodingAgent.Agent and CodingAgent.Web.E2ETests.)
    /// <summary>
    /// When set, replaces the per-job <see cref="AgentProviderFactory"/> that
    /// <see cref="LocalPipelineExecutor"/> normally constructs internally.
    /// Intended for E2E tests that inject <c>FakeProviderFactory</c> (with
    /// <c>ScriptedAgentProvider</c> + <c>InMemoryRepositoryProvider</c>) without
    /// touching production code paths.  Leave <c>null</c> (the default) in all
    /// non-test scenarios.
    /// </summary>
    IProviderFactory? ProviderFactoryOverride = null);
