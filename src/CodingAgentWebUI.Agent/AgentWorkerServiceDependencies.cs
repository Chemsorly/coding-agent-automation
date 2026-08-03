using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="AgentWorkerService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentWorkerServiceDependencies(
    AgentConnectionLifecycle ConnectionLifecycle,
    AgentJobSlotManager SlotManager,
    AgentId AgentId,
    IPipelineExecutor Executor,
    IConsolidationExecutor ConsolidationExecutor,
    IJobCompletionReporter CompletionReporter,
    IKiroCliOrchestrator Orchestrator,
    System.Net.Http.IHttpClientFactory HttpClientFactory,
    IHostApplicationLifetime HostApplicationLifetime,
    Serilog.ILogger Logger);
