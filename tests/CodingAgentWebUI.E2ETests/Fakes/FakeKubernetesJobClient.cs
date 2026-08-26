using System.Collections.Concurrent;
using CodingAgentWebUI.Kubernetes;
using k8s.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// Fake IKubernetesJobClient for K8s-mode E2E tests.
/// Captures CreateJobAsync calls and simulates pod lifecycle.
/// Tests can inspect CreatedJobs, ChatJobs, and configure failure behavior.
/// </summary>
public sealed class FakeKubernetesJobClient : IKubernetesJobClient
{
    /// <summary>All jobs created via CreateJobAsync, keyed by job name.</summary>
    public ConcurrentDictionary<string, V1Job> CreatedJobs { get; } = new();

    /// <summary>
    /// Chat jobs created via CreateJobAsync (job name starts with "caa-chat-"), keyed by job name.
    /// Separate collection for easy test assertions about chat-specific jobs.
    /// </summary>
    public ConcurrentDictionary<string, V1Job> ChatJobs { get; } = new();

    /// <summary>All secrets created via CreateSecretAsync.</summary>
    public ConcurrentBag<V1Secret> CreatedSecrets { get; } = new();

    /// <summary>Jobs that have been deleted via DeleteJobAsync.</summary>
    public ConcurrentBag<string> DeletedJobs { get; } = new();

    /// <summary>Pod logs keyed by pod name. Used by ReadPodLogsAsync.</summary>
    public ConcurrentDictionary<string, string> PodLogs { get; } = new();

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

        // Chat jobs are tracked separately for easy assertions
        if (jobName.StartsWith("caa-chat-", StringComparison.OrdinalIgnoreCase))
            ChatJobs[jobName] = job;

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

    public Task DeleteSecretAsync(string name, string ns, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<V1PodList> ListPodsAsync(string ns, string labelSelector, CancellationToken ct = default)
    {
        return Task.FromResult(new V1PodList { Items = new List<V1Pod>() });
    }

    public Task<string> ReadPodLogsAsync(string podName, string ns, CancellationToken ct = default)
    {
        PodLogs.TryGetValue(podName, out var logs);
        return Task.FromResult(logs ?? string.Empty);
    }

    /// <summary>Resets all state for test isolation.</summary>
    public void Reset()
    {
        CreatedJobs.Clear();
        CreatedSecrets.Clear();
        while (DeletedJobs.TryTake(out _)) { }
        ExistingJobs.Clear();
        PodLogs.Clear();
        CreateJobException = null;
        FailNextCreate = false;
        ChatJobs.Clear();
    }

    /// <summary>
    /// Simulates a chat job reaching a terminal state (Complete or Failed).
    /// Sets Job status conditions so <see cref="ChatJobDispatcher"/>'s background watcher
    /// detects terminal and releases the PVC.
    /// </summary>
    public Task SimulateChatJobTerminalAsync(string jobName, bool success = true)
    {
        if (!ChatJobs.TryGetValue(jobName, out var job))
        {
            // Also check CreatedJobs as fallback
            if (!CreatedJobs.TryGetValue(jobName, out job))
                throw new InvalidOperationException($"Chat job '{jobName}' not found in ChatJobs or CreatedJobs");
        }

        job.Status ??= new k8s.Models.V1JobStatus();
        job.Status.Conditions ??= new List<k8s.Models.V1JobCondition>();

        // Remove any existing Complete/Failed conditions first
        var existing = job.Status.Conditions
            .Where(c => c.Type == "Complete" || c.Type == "Failed")
            .ToList();
        foreach (var c in existing) job.Status.Conditions.Remove(c);

        job.Status.Conditions.Add(new k8s.Models.V1JobCondition
        {
            Type = success ? "Complete" : "Failed",
            Status = "True",
            LastTransitionTime = DateTime.UtcNow
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Finds a chat job by its <c>caa/chat-selector</c> label value (encoded, commas → underscores).
    /// Returns null if no matching job exists.
    /// </summary>
    public V1Job? GetChatJobBySelector(string encodedSelector)
    {
        return ChatJobs.Values.FirstOrDefault(j =>
            j.Metadata?.Labels != null &&
            j.Metadata.Labels.TryGetValue("caa/chat-selector", out var val) &&
            string.Equals(val, encodedSelector, StringComparison.OrdinalIgnoreCase));
    }
}
