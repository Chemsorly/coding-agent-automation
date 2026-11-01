using CodingAgent.Kubernetes;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using k8s.Models;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FakeKubernetesJobClientTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static V1Job BuildJob(string name, params (string Key, string Value)[] labels)
    {
        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = labels.Length > 0
                    ? new Dictionary<string, string>(labels.Select(l => KeyValuePair.Create(l.Key, l.Value)))
                    : null
            }
        };
    }

    // ── ListJobsAsync selector tests ──────────────────────────────────────

    [Fact]
    public async Task ListJobsAsync_WithSelector_ReturnsOnlyMatchingJobs()
    {
        var client = new FakeKubernetesJobClient();
        client.CreatedJobs["a"] = BuildJob("a", ("caa/work-item-id", "1"));
        client.CreatedJobs["b"] = BuildJob("b", ("caa/work-item-id", "2"));

        var result = await client.ListJobsAsync("ns", "caa/work-item-id=1", CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("a", result.Items[0].Metadata!.Name);
    }

    [Fact]
    public async Task ListJobsAsync_WithTwoTermSelector_RequiresBoth()
    {
        var client = new FakeKubernetesJobClient();
        client.CreatedJobs["match"] = BuildJob("match", ("x", "1"), ("y", "2"));
        client.CreatedJobs["partial"] = BuildJob("partial", ("x", "1"));

        var result = await client.ListJobsAsync("ns", "x=1,y=2", CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("match", result.Items[0].Metadata!.Name);
    }

    [Fact]
    public async Task ListJobsAsync_JobWithoutLabels_DoesNotMatchSelector()
    {
        var client = new FakeKubernetesJobClient();
        client.CreatedJobs["no-labels"] = BuildJob("no-labels"); // Labels = null

        var result = await client.ListJobsAsync("ns", "x=1", CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ListJobsAsync_UnsupportedSelector_Throws()
    {
        var client = new FakeKubernetesJobClient();
        client.CreatedJobs["a"] = BuildJob("a", ("x", "1"));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ListJobsAsync("ns", "x!=1", CancellationToken.None));
    }

    [Fact]
    public async Task ListJobsAsync_UnsupportedSelector_NoEquals_Throws()
    {
        // Covers the eqIdx < 0 branch of MatchesSelector: a term with no '=' must throw,
        // not silently return false.
        var client = new FakeKubernetesJobClient();
        client.CreatedJobs["a"] = BuildJob("a", ("x", "1"));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ListJobsAsync("ns", "x", CancellationToken.None));
    }

    [Fact]
    public async Task ListJobsAsync_UnsupportedSelector_EmptyCollection_Throws()
    {
        // Confirms unsupported selectors throw even when the fake holds no jobs (the lazy
        // Where predicate would never run otherwise, masking the error).
        var client = new FakeKubernetesJobClient();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.ListJobsAsync("ns", "x!=1", CancellationToken.None));
    }

    // ── ListPodsAsync selector tests ──────────────────────────────────────

    [Fact]
    public async Task ListPodsAsync_ByJobName_ReturnsOnlyThatJobsPods()
    {
        var client = new FakeKubernetesJobClient();
        client.AddTerminatedPod("job-a", "pod-a1", exitCode: 1);
        client.AddTerminatedPod("job-b", "pod-b1", exitCode: 1);

        var result = await client.ListPodsAsync("ns", "batch.kubernetes.io/job-name=job-a", CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("pod-a1", result.Items[0].Metadata!.Name);
    }

    // ── AddTerminatedPod shape test ───────────────────────────────────────

    [Fact]
    public void AddTerminatedPod_ProducesTheShapeTheReconcilerReads()
    {
        var client = new FakeKubernetesJobClient();

        var pod = client.AddTerminatedPod("job-a", "pod-1", exitCode: 137, reason: "OOMKilled");

        var containerStatus = pod.Status!.ContainerStatuses!
            .Single(s => s.Name == JobSpecBuilder.AgentContainerName);
        Assert.Equal(137, containerStatus.State!.Terminated!.ExitCode);
        Assert.Equal("OOMKilled", containerStatus.State.Terminated.Reason);
    }

    [Fact]
    public async Task AddTerminatedPod_WithLogTail_StoresLogUnderPodName()
    {
        // Exercises the `if (logTail is not null) PodLogs[podName] = logTail` branch.
        var client = new FakeKubernetesJobClient();

        client.AddTerminatedPod("job-a", "pod-1", exitCode: 1, logTail: "container crashed\nOOMKilled");

        // The log must be stored under the pod name so ReadPodLogTailAsync can retrieve it.
        var result = await client.ReadPodLogTailAsync("pod-1", "ns", "agent", tailLines: 10, CancellationToken.None);
        Assert.Equal("container crashed\nOOMKilled", result);
    }

    // ── ReadPodLogTailAsync tests ─────────────────────────────────────────

    [Fact]
    public async Task ReadPodLogTailAsync_ReturnsLastLinesOnly()
    {
        var client = new FakeKubernetesJobClient();
        client.PodLogs["pod-1"] = "a\nb\nc";

        var result = await client.ReadPodLogTailAsync("pod-1", "ns", "agent", tailLines: 2, CancellationToken.None);

        Assert.Equal("b\nc", result);
    }

    [Fact]
    public async Task ReadPodLogTailAsync_LogShorterThanTailLines_ReturnsEntireLog()
    {
        // Covers the `lines.Length <= tailLines ? lines : …` guard: when the stored log has
        // fewer lines than requested, all lines are returned without an ArgumentOutOfRangeException.
        var client = new FakeKubernetesJobClient();
        client.PodLogs["pod-1"] = "only-line";

        var result = await client.ReadPodLogTailAsync("pod-1", "ns", "agent", tailLines: 5, CancellationToken.None);

        Assert.Equal("only-line", result);
    }

    // ── SimulateWorkItemJobFailedAsync tests ──────────────────────────────

    [Fact]
    public async Task SimulateWorkItemJobFailedAsync_SetsReasonAndMessage()
    {
        var client = new FakeKubernetesJobClient();
        await client.CreateJobAsync(BuildJob("test-job"), "ns", CancellationToken.None);

        await client.SimulateWorkItemJobFailedAsync(
            "test-job",
            reason: "DeadlineExceeded",
            message: "Job was active longer than specified deadline");

        var condition = client.CreatedJobs["test-job"].Status!.Conditions!
            .Single(c => c.Type == "Failed");
        Assert.Equal("True", condition.Status);
        Assert.Equal("DeadlineExceeded", condition.Reason);
        Assert.Equal("Job was active longer than specified deadline", condition.Message);
    }

    // ── SimulateWorkItemJobSucceededAsync tests ───────────────────────────

    [Fact]
    public async Task SimulateWorkItemJobSucceededAsync_ReplacesFailedWithComplete()
    {
        var client = new FakeKubernetesJobClient();
        await client.CreateJobAsync(BuildJob("job"), "ns", CancellationToken.None);
        await client.SimulateWorkItemJobFailedAsync("job");

        await client.SimulateWorkItemJobSucceededAsync("job");

        var conditions = client.CreatedJobs["job"].Status!.Conditions!;
        Assert.DoesNotContain(conditions, c => c.Type == "Failed");
        var complete = Assert.Single(conditions, c => c.Type == "Complete");
        Assert.Equal("True", complete.Status);
    }

    // ── CreateJobAsync failure mode tests ─────────────────────────────────

    [Fact]
    public async Task CreateJobAsync_FailNextCreate_FailsOnceWith500()
    {
        var client = new FakeKubernetesJobClient();
        client.FailNextCreate = true;

        var ex = await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() =>
            client.CreateJobAsync(BuildJob("job-1"), "ns", CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, ex.Response.StatusCode);

        // Second call succeeds
        await client.CreateJobAsync(BuildJob("job-2"), "ns", CancellationToken.None);
        Assert.True(client.CreatedJobs.ContainsKey("job-2"));
    }

    [Fact]
    public async Task CreateJobAsync_CreateJobException_ThrowsThatException()
    {
        var client = new FakeKubernetesJobClient();
        var expected = new InvalidOperationException("injected failure");
        client.CreateJobException = expected;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CreateJobAsync(BuildJob("job"), "ns", CancellationToken.None));
        Assert.Same(expected, actual);
    }

    // ── AgentJobPvcs integration test ─────────────────────────────────────

    [Fact]
    public async Task AgentJobPvcs_ListHeld_SeesOnlyAgentJobsThatHaveNotEnded()
    {
        var client = new FakeKubernetesJobClient();

        // job-a: agent-job label, non-terminal — PVC pvc-a must be returned
        var jobA = BuildJob("job-a", ("app.kubernetes.io/component", "agent-job"));
        jobA.Spec = new V1JobSpec
        {
            Template = new V1PodTemplateSpec
            {
                Spec = new V1PodSpec
                {
                    Volumes = new List<V1Volume>
                    {
                        new V1Volume
                        {
                            Name = "pvc-vol-a",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-a" }
                        }
                    }
                }
            }
        };
        await client.CreateJobAsync(jobA, "ns", CancellationToken.None);

        // job-b: agent-job label, but terminal (succeeded) — PVC pvc-b must NOT be returned
        var jobB = BuildJob("job-b", ("app.kubernetes.io/component", "agent-job"));
        jobB.Spec = new V1JobSpec
        {
            Template = new V1PodTemplateSpec
            {
                Spec = new V1PodSpec
                {
                    Volumes = new List<V1Volume>
                    {
                        new V1Volume
                        {
                            Name = "pvc-vol-b",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-b" }
                        }
                    }
                }
            }
        };
        await client.CreateJobAsync(jobB, "ns", CancellationToken.None);
        await client.SimulateWorkItemJobSucceededAsync("job-b");

        // job-c: no agent-job label — PVC pvc-c must NOT be returned
        var jobC = BuildJob("job-c"); // no label
        jobC.Spec = new V1JobSpec
        {
            Template = new V1PodTemplateSpec
            {
                Spec = new V1PodSpec
                {
                    Volumes = new List<V1Volume>
                    {
                        new V1Volume
                        {
                            Name = "pvc-vol-c",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-c" }
                        }
                    }
                }
            }
        };
        await client.CreateJobAsync(jobC, "ns", CancellationToken.None);

        var held = await AgentJobPvcs.ListHeldAsync(client, "ns", CancellationToken.None);

        Assert.Equal(new HashSet<string> { "pvc-a" }, held);
    }

    // ── Reset tests ───────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsPods()
    {
        var client = new FakeKubernetesJobClient();
        client.AddTerminatedPod("job", "pod-1", exitCode: 0);
        Assert.Single(client.Pods);

        client.Reset();

        Assert.True(client.Pods.IsEmpty);
    }
}
