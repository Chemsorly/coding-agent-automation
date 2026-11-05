using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Spec 057 DD3, DD4: the one gate every agent start passes, work-item dispatch and chat launch alike. It takes the
/// <c>dispatch-agents</c> lock, counts the running agent Jobs, reads the UI limits and answers the limit checks. The
/// Helm limits are fixed for the process (they change only with a restart, which the chart's checksum annotation
/// triggers); the UI limits are read at every start.
/// </summary>
internal sealed class AgentStartGate : IAgentStartGate
{
    /// <summary>The lock every agent start takes.</summary>
    internal const string LockName = "dispatch-agents";

    private readonly IDistributedLockProvider _locks;
    private readonly IKubernetesJobClient _jobs;
    private readonly DispatchServiceOptions _options;
    private readonly AgentCapacityStore _capacity;
    private readonly Serilog.ILogger _log;
    private readonly IReadOnlyCollection<string> _keys;
    private readonly IReadOnlyDictionary<string, int> _helm;

    public AgentStartGate(
        IDistributedLockProvider locks,
        IKubernetesJobClient jobs,
        JobTemplateStore templates,
        DispatchServiceOptions options,
        AgentCapacityStore capacity,
        Serilog.ILogger log)
    {
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(log);
        _locks = locks;
        _jobs = jobs;
        _options = options;
        _capacity = capacity;
        _log = log;
        _keys = templates.ToolsByKey().Keys.ToArray();
        _helm = templates.MaxConcurrentByKey();

        foreach (var group in AgentJobSnapshot.LabelCollisions(_keys))
        {
            _log.Warning("AgentStartGate: job templates {Keys} share the Job label {Label}; they share one count",
                group, AgentJobSnapshot.LabelValue(group[0]));
        }
    }

    /// <inheritdoc />
    public async Task<IAgentStartLease> EnterAsync(CancellationToken ct)
    {
        var handle = await _locks.AcquireAsync(LockName, ct);
        try
        {
            AgentJobSnapshot jobs;
            try
            {
                jobs = await AgentJobSnapshot.ReadAsync(_jobs, _options.Namespace, _keys, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Includes a client timeout that surfaces as a cancellation while ct is not cancelled.
                _log.Warning(ex, "AgentStartGate: could not list the agent Jobs — no agent started");
                throw new AgentCountUnavailableException(ex);
            }

            var ui = await _capacity.LoadAsync(ct);
            var running = jobs.Counts.ByTemplate.ToDictionary(
                kv => kv.Key, kv => kv.Value.Runs + kv.Value.Chats, StringComparer.Ordinal);
            var inputs = new AgentCapacityInputs(_options.MaxConcurrentAgents, ui, _helm, running, jobs.Counts.Total);
            return new Lease(handle, jobs, inputs);
        }
        catch
        {
            // Never leak the start lock: every later dispatch and chat would wait for it.
            await handle.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(IAsyncDisposable handle, AgentJobSnapshot jobs, AgentCapacityInputs inputs) : IAgentStartLease
    {
        private int _disposed;

        public AgentJobSnapshot Jobs { get; } = jobs;

        public AgentStartDecision CheckWorkItem(string templateKey) => AgentCapacityRules.CheckWorkItem(inputs, templateKey);

        public AgentStartDecision CheckChat(string templateKey) => AgentCapacityRules.CheckChat(inputs, templateKey);

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? handle.DisposeAsync() : ValueTask.CompletedTask;
    }
}
