using System.Collections.Concurrent;
using CodingAgent.Kubernetes;
using k8s.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

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

    /// <summary>Pod logs keyed by pod name. Used by ReadPodLogTailAsync.</summary>
    public ConcurrentDictionary<string, string> PodLogs { get; } = new();

    /// <summary>Pods returned by ListPodsAsync, keyed by pod name.</summary>
    public ConcurrentDictionary<string, V1Pod> Pods { get; } = new();

    /// <summary>If set, CreateJobAsync will throw this exception.</summary>
    public Exception? CreateJobException { get; set; }

    /// <summary>If true, next CreateJobAsync call fails (resets after one failure).</summary>
    public bool FailNextCreate { get; set; }

    /// <summary>Jobs to return from ListJobsAsync (simulates existing jobs in cluster).</summary>
    public List<V1Job> ExistingJobs { get; } = new();

    /// <summary>
    /// When non-null, <see cref="CreateJobAsync"/> records the job and fires events, then awaits
    /// this TCS before returning. This holds the dispatch caller inside <c>CreateJobAsync</c> so
    /// the DB write of <c>Dispatched</c> has not yet happened — simulating the race window that
    /// issue #2950 fixed.
    ///
    /// <para>
    /// Must be set to <c>null</c> after each test (handled by <see cref="Reset"/>).
    /// </para>
    /// </summary>
    public TaskCompletionSource? AfterCreateDelay { get; set; }

    /// <summary>
    /// Raised synchronously inside <see cref="CreateJobAsync"/> immediately after the job is
    /// recorded in <see cref="ChatJobs"/>. Each subscriber receives the created <see cref="V1Job"/>.
    /// Used by <c>DispatchChatPodAndConnectAsync</c> to claim ownership of the specific job that
    /// was created by its dispatch call, without relying on insertion-order iteration.
    /// </summary>
    public event Action<V1Job>? ChatJobCreated;

    /// <summary>
    /// Raised synchronously inside <see cref="CreateJobAsync"/> for work-item jobs (those whose
    /// name does NOT start with <c>"caa-chat-"</c>). Each subscriber receives the created
    /// <see cref="V1Job"/>. Used by <c>JobControllerE2ETests</c> to bootstrap a
    /// <see cref="CodingAgent.Web.E2ETests.Infrastructure.FakeAgentClient"/> reactively from the
    /// exact job created by the dispatch call, without polling.
    /// </summary>
    public event Action<V1Job>? WorkItemJobCreated;

    public async Task CreateJobAsync(V1Job job, string ns, CancellationToken ct = default)
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

        // Simulate what the Kubernetes API server does: stamp CreationTimestamp on the object.
        // CleanupOrphansAsync uses this as the fallback anchor for brand-new jobs that have no
        // StartTime yet (the #2950 fix). Without this stamp, a freshly-created job would appear
        // as an orphan with no timestamp anchor and be deleted immediately.
        // Tests that want to simulate the pre-fix behaviour can clear this explicitly:
        //   Fixture.K8sClient.CreatedJobs[jobName].Metadata.CreationTimestamp = null;
        if (job.Metadata is not null)
            job.Metadata.CreationTimestamp ??= DateTime.UtcNow;

        CreatedJobs[jobName] = job;

        // Chat jobs are tracked separately for easy assertions
        if (jobName.StartsWith("caa-chat-", StringComparison.OrdinalIgnoreCase))
        {
            ChatJobs[jobName] = job;
            ChatJobCreated?.Invoke(job);
        }
        else
        {
            // Work-item job — notify subscribers (e.g. JobControllerE2ETests agent bootstrap)
            WorkItemJobCreated?.Invoke(job);
        }

        // If a delay gate is set, hold here until the test releases it.
        // This lets the test call CleanupOrphansAsync while the WorkItem is still Pending
        // (before DispatchLifecycleService commits the Dispatched status write).
        if (AfterCreateDelay is not null)
            await AfterCreateDelay.Task.WaitAsync(ct);
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
        // Validate before the Where so unsupported selectors throw even when the collection is empty
        // (a lazy Where over an empty sequence never invokes the predicate, masking the error).
        ValidateSelector(labelSelector);
        var jobs = CreatedJobs.Values.Concat(ExistingJobs)
            .Where(j => MatchesSelector(j.Metadata?.Labels, labelSelector))
            .ToList();
        return Task.FromResult(new V1JobList { Items = jobs });
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
        // Validate before the Where for the same reason as ListJobsAsync.
        ValidateSelector(labelSelector);
        var pods = Pods.Values
            .Where(p => MatchesSelector(p.Metadata?.Labels, labelSelector))
            .ToList();
        return Task.FromResult(new V1PodList { Items = pods });
    }

    public Task<string> ReadPodLogTailAsync(string podName, string ns, string container, int tailLines, CancellationToken ct = default)
    {
        if (!PodLogs.TryGetValue(podName, out var logs))
            return Task.FromResult(string.Empty);
        var lines = logs.Split('\n');
        var tail = lines.Length <= tailLines ? lines : lines[^tailLines..];
        return Task.FromResult(string.Join('\n', tail));
    }

    /// <summary>Resets all state for test isolation.</summary>
    public void Reset()
    {
        CreatedJobs.Clear();
        CreatedSecrets.Clear();
        while (DeletedJobs.TryTake(out _)) { }
        ExistingJobs.Clear();
        PodLogs.Clear();
        Pods.Clear();
        CreateJobException = null;
        FailNextCreate = false;
        ChatJobs.Clear();
        ChatJobCreated = null;
        // New fields — must be cleared to prevent inter-test leakage
        WorkItemJobCreated = null;
        AfterCreateDelay = null;
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
    /// Adds a pod of <paramref name="jobName"/> whose agent container has terminated, as the kubelet reports it.
    /// The returned pod can be changed further, for example to add conditions.
    /// </summary>
    public V1Pod AddTerminatedPod(
        string jobName,
        string podName,
        int exitCode,
        string? reason = "Error",
        DateTime? finishedAt = null,
        string? logTail = null)
    {
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                Labels = new Dictionary<string, string>
                {
                    ["batch.kubernetes.io/job-name"] = jobName
                }
            },
            Status = new V1PodStatus
            {
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new V1ContainerStatus
                    {
                        Name = JobSpecBuilder.AgentContainerName,
                        State = new V1ContainerState
                        {
                            Terminated = new V1ContainerStateTerminated
                            {
                                ExitCode = exitCode,
                                Reason = reason,
                                FinishedAt = finishedAt ?? DateTime.UtcNow
                            }
                        }
                    }
                }
            }
        };
        Pods[podName] = pod;
        if (logTail is not null)
            PodLogs[podName] = logTail;
        return pod;
    }

    /// <summary>
    /// Simulates a work-item job reaching a <c>Failed</c> terminal state.
    /// Sets the <c>Failed</c> condition so <see cref="CodingAgent.JobController.Reconciliation.ReconciliationLoop"/>
    /// detects it during <c>ReconcileOnceAsync</c>.
    /// </summary>
    public Task SimulateWorkItemJobFailedAsync(string jobName, string? reason = null, string? message = null)
    {
        if (!CreatedJobs.TryGetValue(jobName, out var job))
            throw new InvalidOperationException($"Job '{jobName}' not found in CreatedJobs");

        job.Status ??= new k8s.Models.V1JobStatus();
        job.Status.Conditions ??= new List<k8s.Models.V1JobCondition>();

        // Remove any existing Complete/Failed conditions first
        var existing = job.Status.Conditions
            .Where(c => c.Type == "Complete" || c.Type == "Failed")
            .ToList();
        foreach (var c in existing) job.Status.Conditions.Remove(c);

        job.Status.Conditions.Add(new k8s.Models.V1JobCondition
        {
            Type = "Failed",
            Status = "True",
            Reason = reason,
            Message = message,
            LastTransitionTime = DateTime.UtcNow
        });

        return Task.CompletedTask;
    }

    /// <summary>Sets the Complete condition on a work-item Job, as Kubernetes does when its pod exits 0.</summary>
    public Task SimulateWorkItemJobSucceededAsync(string jobName)
    {
        if (!CreatedJobs.TryGetValue(jobName, out var job))
            throw new InvalidOperationException($"Job '{jobName}' not found in CreatedJobs");

        job.Status ??= new k8s.Models.V1JobStatus();
        job.Status.Conditions ??= new List<k8s.Models.V1JobCondition>();

        // Remove any existing Complete/Failed conditions first
        var existing = job.Status.Conditions
            .Where(c => c.Type == "Complete" || c.Type == "Failed")
            .ToList();
        foreach (var c in existing) job.Status.Conditions.Remove(c);

        job.Status.Conditions.Add(new k8s.Models.V1JobCondition
        {
            Type = "Complete",
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

    /// <summary>Validates that a label selector uses only supported equality operators. Throws <see cref="NotSupportedException"/> for unsupported operators ("!=") or terms with no "=". An empty selector is valid. Call this before any LINQ Where to ensure the throw is unconditional even when the sequence is empty.</summary>
    private static void ValidateSelector(string? labelSelector)
    {
        if (string.IsNullOrEmpty(labelSelector))
            return;
        foreach (var term in labelSelector.Split(','))
        {
            if (term.Contains("!="))
                throw new NotSupportedException($"Unsupported label selector: {labelSelector}");
            if (term.IndexOf('=') < 0)
                throw new NotSupportedException($"Unsupported label selector: {labelSelector}");
        }
    }

    /// <summary>Matches a Kubernetes equality selector: "a=b" or "a=b,c=d". An empty selector matches everything.</summary>
    private static bool MatchesSelector(IDictionary<string, string>? labels, string? labelSelector)
    {
        if (string.IsNullOrEmpty(labelSelector))
            return true;
        if (labels is null)
            return false;

        foreach (var term in labelSelector.Split(','))
        {
            if (term.Contains("!="))
                throw new NotSupportedException($"Unsupported label selector: {labelSelector}");

            var eqIdx = term.IndexOf('=');
            if (eqIdx < 0)
                throw new NotSupportedException($"Unsupported label selector: {labelSelector}");

            var key = term[..eqIdx];
            var value = term[(eqIdx + 1)..];
            if (!labels.TryGetValue(key, out var actual) || actual != value)
                return false;
        }

        return true;
    }
}
