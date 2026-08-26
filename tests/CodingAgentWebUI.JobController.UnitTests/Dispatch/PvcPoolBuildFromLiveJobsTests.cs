using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.Kubernetes;
using k8s.Models;
using Moq;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="PvcPool.BuildFromLiveJobsAsync"/>.
/// Verifies the static factory method correctly initialises the claimed-set from live K8s Jobs.
/// </summary>
public sealed class PvcPoolBuildFromLiveJobsTests
{
    private static DispatchServiceOptions OptionsWithPvcs(params string[] pvcs) =>
        new() { Namespace = "default", KiroPvcPool = [.. pvcs] };

    private static V1Job JobWithPvcVolumes(params string[] claimNames) =>
        new()
        {
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Volumes = claimNames
                            .Select(name => new V1Volume
                            {
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = name }
                            })
                            .ToList()
                    }
                }
            }
        };

    // ── Empty pool ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_EmptyKiroPvcPool_ReturnsTotalCountZero_NeverCallsK8s()
    {
        var k8sClient = new Mock<IKubernetesJobClient>(MockBehavior.Strict);
        var opts = OptionsWithPvcs(); // empty pool

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(0);
        pool.AvailableCount.Should().Be(0);

        // Strict mock: no calls should have been made
        k8sClient.VerifyNoOtherCalls();
    }

    // ── No live jobs ──────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_NoLiveJobs_AllPvcsAvailable()
    {
        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var opts = OptionsWithPvcs("pvc-1", "pvc-2", "pvc-3");

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(3);
        pool.AvailableCount.Should().Be(3, "no live jobs means no PVCs are claimed");
    }

    // ── Live job claims a pool PVC ────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_LiveJobWithPoolPvc_ThatPvcIsClaimed()
    {
        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [JobWithPvcVolumes("pvc-1")] });

        var opts = OptionsWithPvcs("pvc-1", "pvc-2");

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(2);
        pool.AvailableCount.Should().Be(1, "pvc-1 is claimed by the live job");
    }

    // ── Foreign PVC ignored ───────────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_LiveJobWithForeignPvc_Ignored_AllPoolPvcsAvailable()
    {
        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [JobWithPvcVolumes("foreign-pvc-not-in-pool")] });

        var opts = OptionsWithPvcs("pvc-1", "pvc-2");

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(2);
        pool.AvailableCount.Should().Be(2, "foreign PVCs must not affect the pool's claimed-set");
    }

    // ── K8s client throws ─────────────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_K8sClientThrows_ExceptionSwallowed_AllPvcsAvailable()
    {
        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s API unavailable"));

        var opts = OptionsWithPvcs("pvc-1", "pvc-2");

        // Must not propagate the exception
        var act = async () => await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(2);
        pool.AvailableCount.Should().Be(2,
            "failed rebuild starts with an empty claimed-set so all PVCs are available");
    }

    // ── Duplicate PVC volumes ─────────────────────────────────────────────────

    [Fact]
    public async Task BuildFromLiveJobsAsync_DuplicatePvcVolumesInSameJob_ClaimedOnlyOnce()
    {
        // A job mounting the same PVC twice (defensive test) must not over-claim.
        var jobWithDuplicatePvc = JobWithPvcVolumes("pvc-1", "pvc-1");

        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient
            .Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [jobWithDuplicatePvc] });

        var opts = OptionsWithPvcs("pvc-1", "pvc-2");

        var pool = await PvcPool.BuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.TotalCount.Should().Be(2);
        pool.AvailableCount.Should().Be(1,
            "duplicate PVC volumes in the same job must count as a single claim");
    }
}
