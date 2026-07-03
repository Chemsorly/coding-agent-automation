using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Dispatch;
using k8s.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// Fake IKubernetesJobClient for K8s-mode E2E tests.
/// Captures CreateJobAsync calls and simulates pod lifecycle.
/// Tests can inspect CreatedJobs and configure failure behavior.
/// </summary>
public sealed class FakeKubernetesJobClient : IKubernetesJobClient
{
    /// <summary>All jobs created via CreateJobAsync, keyed by job name.</summary>
    public ConcurrentDictionary<string, V1Job> CreatedJobs { get; } = new();

    /// <summary>All secrets created via CreateSecretAsync.</summary>
    public ConcurrentBag<V1Secret> CreatedSecrets { get; } = new();

    /// <summary>Jobs that have been deleted via DeleteJobAsync.</summary>
    public ConcurrentBag<string> DeletedJobs { get; } = new();

    /// <summary>If set, CreateJobAsync will throw this exception.</summary>
    public Exception? CreateJobException { get; set; }

    /// <summary>If true, next CreateJobAsync call fails (resets after one failure).</summary>
    public bool FailNextCreate { get; set; }

    /// <summary>Jobs to return from ListJobsAsync (simulates existing jobs in cluster).</summary>
    public List<V1Job> ExistingJobs { get; } = new();

    public Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
    {
        if (CreateJobException is not null)
            throw CreateJobException;

        if (FailNextCreate)
        {
            FailNextCreate = false;
            throw new k8s.Autorest.HttpOperationException("Simulated K8s API failure")
            {
                Response = new k8s.Autorest.HttpResponseMessageWrapper(
                    new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError), "")
            };
        }

        var jobName = job.Metadata?.Name ?? $"job-{Guid.NewGuid()}";
        CreatedJobs[jobName] = job;
        return Task.CompletedTask;
    }

    public Task DeleteJobAsync(string name, string ns, CancellationToken ct = default)
    {
        DeletedJobs.Add(name);
        CreatedJobs.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<V1Job> ReadJobAsync(string name, string ns, CancellationToken ct = default)
    {
        if (CreatedJobs.TryGetValue(name, out var job))
            return Task.FromResult(job);

        var existing = ExistingJobs.FirstOrDefault(j => j.Metadata?.Name == name);
        if (existing is not null)
            return Task.FromResult(existing);

        throw new k8s.Autorest.HttpOperationException("Job not found")
        {
            Response = new k8s.Autorest.HttpResponseMessageWrapper(
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound), "")
        };
    }

    public Task<V1JobList> ListJobsAsync(string ns, string labelSelector, CancellationToken ct = default)
    {
        var allJobs = CreatedJobs.Values.Concat(ExistingJobs).ToList();
        return Task.FromResult(new V1JobList { Items = allJobs });
    }

    public Task CreateSecretAsync(V1Secret secret, string ns, CancellationToken ct = default)
    {
        CreatedSecrets.Add(secret);
        return Task.CompletedTask;
    }

    public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
    {
        return Task.FromResult(new V1PodList { Items = new List<V1Pod>() });
    }

    /// <summary>Resets all state for test isolation.</summary>
    public void Reset()
    {
        CreatedJobs.Clear();
        CreatedSecrets.Clear();
        while (DeletedJobs.TryTake(out _)) { }
        ExistingJobs.Clear();
        CreateJobException = null;
        FailNextCreate = false;
    }
}
