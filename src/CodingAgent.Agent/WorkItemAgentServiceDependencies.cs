using CodingAgent.Infrastructure;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Agent;

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
