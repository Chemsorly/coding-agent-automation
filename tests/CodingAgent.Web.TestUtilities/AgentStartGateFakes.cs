using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Spec 057: test doubles for <see cref="IAgentStartGate"/>. Every method returns a new gate; never share one between
/// tests. Each gate serializes its leases like the real lock does, so concurrency tests keep their meaning.
/// </summary>
public static class AgentStartGateFakes
{
    /// <summary>
    /// Every check allows. When <paramref name="jobs"/> is given, the lease's snapshot (and with it the held PVCs) is read
    /// from that client through the real <see cref="AgentJobSnapshot.ReadAsync"/>, so tests that mock
    /// <c>ListJobsAsync</c> keep working; a list failure becomes <see cref="AgentCountUnavailableException"/>.
    /// <paramref name="onEnter"/> runs after the lock is taken (e.g. to change state "behind the lock").
    /// </summary>
    public static FakeAgentStartGate AllowAll(
        IKubernetesJobClient? jobs = null, string ns = "default", Func<CancellationToken, Task>? onEnter = null) =>
        new(_ => AgentStartDecision.Allow, _ => AgentStartDecision.Allow, jobs, ns, onEnter, enterException: null);

    /// <summary>Every check answers <paramref name="decision"/>.</summary>
    public static FakeAgentStartGate Deny(AgentStartDecision decision) =>
        new(_ => decision, _ => decision, jobs: null, ns: "default", onEnter: null, enterException: null);

    /// <summary><see cref="IAgentStartGate.EnterAsync"/> throws <see cref="AgentCountUnavailableException"/>.</summary>
    public static FakeAgentStartGate Unavailable() =>
        new(_ => AgentStartDecision.Allow, _ => AgentStartDecision.Allow, null, "default", null,
            () => new AgentCountUnavailableException(new InvalidOperationException("Simulated Job list failure")));

    /// <summary><see cref="IAgentStartGate.EnterAsync"/> throws <see cref="TimeoutException"/> (lock not acquired).</summary>
    public static FakeAgentStartGate LockTimeout() =>
        new(_ => AgentStartDecision.Allow, _ => AgentStartDecision.Allow, null, "default", null,
            () => new TimeoutException("Simulated lock timeout"));

    /// <summary>The real <see cref="AgentCapacityRules"/> over <paramref name="inputs"/>.</summary>
    public static FakeAgentStartGate FromInputs(AgentCapacityInputs inputs, AgentJobSnapshot? jobs = null) =>
        new(key => AgentCapacityRules.CheckWorkItem(inputs, key), key => AgentCapacityRules.CheckChat(inputs, key),
            null, "default", null, null, jobs);

    /// <summary>An empty snapshot: no Jobs, no held PVCs.</summary>
    public static AgentJobSnapshot EmptySnapshot { get; } = new(
        new AgentJobCounts(new Dictionary<string, (int Runs, int Chats)>(StringComparer.Ordinal), (0, 0), 0),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>A configurable <see cref="IAgentStartGate"/>; see <see cref="AgentStartGateFakes"/>.</summary>
public sealed class FakeAgentStartGate : IAgentStartGate
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<string, AgentStartDecision> _checkWorkItem;
    private readonly Func<string, AgentStartDecision> _checkChat;
    private readonly IKubernetesJobClient? _jobs;
    private readonly string _ns;
    private readonly Func<CancellationToken, Task>? _onEnter;
    private readonly Func<Exception>? _enterException;
    private readonly AgentJobSnapshot? _snapshot;
    private int _entered;
    private int _disposed;

    internal FakeAgentStartGate(
        Func<string, AgentStartDecision> checkWorkItem,
        Func<string, AgentStartDecision> checkChat,
        IKubernetesJobClient? jobs,
        string ns,
        Func<CancellationToken, Task>? onEnter,
        Func<Exception>? enterException,
        AgentJobSnapshot? snapshot = null)
    {
        _checkWorkItem = checkWorkItem;
        _checkChat = checkChat;
        _jobs = jobs;
        _ns = ns;
        _onEnter = onEnter;
        _enterException = enterException;
        _snapshot = snapshot;
    }

    /// <summary>How many leases were entered.</summary>
    public int Entered => Volatile.Read(ref _entered);

    /// <summary>How many leases were disposed (each counted once).</summary>
    public int Disposed => Volatile.Read(ref _disposed);

    /// <inheritdoc />
    public async Task<IAgentStartLease> EnterAsync(CancellationToken ct)
    {
        if (_enterException is not null)
            throw _enterException();

        await _lock.WaitAsync(ct);
        try
        {
            if (_onEnter is not null)
                await _onEnter(ct);

            var snapshot = _snapshot ?? AgentStartGateFakes.EmptySnapshot;
            if (_jobs is not null)
            {
                try
                {
                    snapshot = await AgentJobSnapshot.ReadAsync(_jobs, _ns, Array.Empty<string>(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    throw new AgentCountUnavailableException(ex);
                }
            }

            Interlocked.Increment(ref _entered);
            return new Lease(this, snapshot);
        }
        catch
        {
            _lock.Release();
            throw;
        }
    }

    private sealed class Lease(FakeAgentStartGate gate, AgentJobSnapshot jobs) : IAgentStartLease
    {
        private int _released;

        public AgentJobSnapshot Jobs { get; } = jobs;

        public AgentStartDecision CheckWorkItem(string templateKey) => gate._checkWorkItem(templateKey);

        public AgentStartDecision CheckChat(string templateKey) => gate._checkChat(templateKey);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Increment(ref gate._disposed);
                gate._lock.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
