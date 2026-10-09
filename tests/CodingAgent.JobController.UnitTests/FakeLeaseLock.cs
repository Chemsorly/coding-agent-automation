using k8s.LeaderElection;

namespace CodingAgent.JobController.UnitTests;

/// <summary>
/// In-memory <see cref="ILock"/> that drives <see cref="LeaderElectionService"/>'s election loop
/// without a Kubernetes API server. Thread-safe: the k8s LeaderElector calls it from thread-pool threads.
/// </summary>
internal sealed class FakeLeaseLock : ILock
{
    private readonly Lock _gate = new();
    private LeaderElectionRecord? _record;

    public FakeLeaseLock(string identity) => Identity = identity;

    public string Identity { get; }

    /// <summary>Identity holding the lease now, or null when nobody holds it.</summary>
    public string? CurrentHolder
    {
        get
        {
            lock (_gate)
                return _record?.HolderIdentity;
        }
    }

    public Task<LeaderElectionRecord> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_record!); // null = no Lease yet; the elector then calls CreateAsync
    }

    public Task<bool> CreateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_record is not null)
                return Task.FromResult(false);
            _record = record;
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateAsync(LeaderElectionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Like the API server's optimistic concurrency (HTTP 409 in LeaseLock): a renewal that would
            // overwrite a lease another identity holds is rejected.
            if (_record is not null && _record.HolderIdentity != record.HolderIdentity)
                return Task.FromResult(false);
            _record = record;
            return Task.FromResult(true);
        }
    }

    public string Describe() => $"fake-lease/{Identity}";

    /// <summary>Simulates another replica taking the lease and holding it for an hour.</summary>
    public void StealLease(string holderIdentity)
    {
        lock (_gate)
        {
            _record = new LeaderElectionRecord
            {
                HolderIdentity = holderIdentity,
                LeaseDurationSeconds = 3600,
                AcquireTime = DateTime.UtcNow,
                RenewTime = DateTime.UtcNow,
                LeaderTransitions = (_record?.LeaderTransitions ?? 0) + 1,
            };
        }
    }

    /// <summary>Simulates the holder giving the lease up (the Lease object is gone).</summary>
    public void ReleaseLease()
    {
        lock (_gate)
            _record = null;
    }
}
