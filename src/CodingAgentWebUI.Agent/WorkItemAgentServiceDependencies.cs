using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Groups the core dependencies of <see cref="WorkItemAgentService"/> to reduce
/// constructor parameter count (S107). <see cref="ServiceProvider"/> is optional.
/// </summary>
public sealed record WorkItemAgentServiceDependencies(
    string WorkItemId,
    IWorkItemLifecycleClient WorkItemClient,
    IAgentConnectionManager ConnectionManager,
    IWorkItemExecutor WorkItemExecutor,
    IJobCompletionReporter CompletionReporter,
    AgentId AgentId,
    IHostApplicationLifetime Lifetime,
    Serilog.ILogger Logger,
    IServiceProvider? ServiceProvider = null);
