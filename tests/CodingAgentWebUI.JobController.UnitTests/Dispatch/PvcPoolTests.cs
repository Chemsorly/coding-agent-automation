using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;
using CodingAgentWebUI.Kubernetes;
using k8s.Models;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="PvcPool"/> — in-memory PVC lifecycle management.
/// </summary>
public sealed class PvcPoolTests
{
    // ── TryClaim ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryClaim_AvailablePvc_ReturnsPvcName()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);

        var claimed = pool.TryClaim(Guid.NewGuid());

        claimed.Should().NotBeNull();
        claimed.Should().BeOneOf("pvc-1", "pvc-2");
    }

    [Fact]
    public void TryClaim_ExhaustedPool_ReturnsNull()
    {
        var pool = new PvcPool(["pvc-1"]);
        pool.TryClaim(Guid.NewGuid()); // claim the only one

        var second = pool.TryClaim(Guid.NewGuid());

        second.Should().BeNull("pool is exhausted after first claim");
    }

    [Fact]
    public void TryClaim_EmptyPool_ReturnsNull()
    {
        var pool = new PvcPool([]);

        var claimed = pool.TryClaim(Guid.NewGuid());

        claimed.Should().BeNull();
    }

    [Fact]
    public void TryClaim_ClaimsDistinctPvcs_ForMultipleCallers()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);

        var first = pool.TryClaim(Guid.NewGuid());
        var second = pool.TryClaim(Guid.NewGuid());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().NotBe(second, "each caller should receive a distinct PVC");
    }

    // ── Release ───────────────────────────────────────────────────────────────

    [Fact]
    public void Release_ClaimedPvc_MakesItAvailableAgain()
    {
        var pool = new PvcPool(["pvc-1"]);
        var claimed = pool.TryClaim(Guid.NewGuid());
        pool.AvailableCount.Should().Be(0);

        pool.Release(claimed!);

        pool.AvailableCount.Should().Be(1);
    }

    [Fact]
    public void Release_UnknownPvc_IsNoOp()
    {
        var pool = new PvcPool(["pvc-1"]);

        // Should not throw
        pool.Release("pvc-not-in-pool");

        pool.AvailableCount.Should().Be(1, "pool state unchanged after releasing unknown PVC");
    }

    [Fact]
    public void Release_EmptyString_IsNoOp()
    {
        var pool = new PvcPool(["pvc-1"]);

        pool.Release(""); // should not throw

        pool.AvailableCount.Should().Be(1);
    }

    // ── AvailableCount ────────────────────────────────────────────────────────

    [Fact]
    public void AvailableCount_ReflectsClaimAndReleaseCycle()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2", "pvc-3"]);

        pool.AvailableCount.Should().Be(3);

        var p1 = pool.TryClaim(Guid.NewGuid());
        pool.AvailableCount.Should().Be(2);

        var p2 = pool.TryClaim(Guid.NewGuid());
        pool.AvailableCount.Should().Be(1);

        pool.Release(p1!);
        pool.AvailableCount.Should().Be(2);

        pool.Release(p2!);
        pool.AvailableCount.Should().Be(3);
    }

    // ── TotalCount ────────────────────────────────────────────────────────────

    [Fact]
    public void TotalCount_EqualsInitialPoolSize_RegardlessOfClaims()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.TryClaim(Guid.NewGuid());

        pool.TotalCount.Should().Be(2, "TotalCount is the pool size, not the available count");
    }

    // ── RebuildFromLiveJobsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RebuildFromLiveJobsAsync_NoJobs_ClearsClaimedSet()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.TryClaim(Guid.NewGuid()); // claim one

        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [] });

        var opts = new DispatchServiceOptions { Namespace = "default", KiroPvcPool = ["pvc-1", "pvc-2"] };
        await pool.RebuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.AvailableCount.Should().Be(2, "rebuild with no live jobs should clear the claimed set");
    }

    [Fact]
    public async Task RebuildFromLiveJobsAsync_JobsWithPvcVolumes_RebuildsClaimedSet()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);

        var jobWithPvc = new V1Job
        {
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Volumes =
                        [
                            new V1Volume
                            {
                                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-1" }
                            }
                        ]
                    }
                }
            }
        };

        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = [jobWithPvc] });

        var opts = new DispatchServiceOptions { Namespace = "default", KiroPvcPool = ["pvc-1", "pvc-2"] };
        await pool.RebuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        pool.AvailableCount.Should().Be(1, "pvc-1 is claimed by the live job");
    }

    [Fact]
    public async Task RebuildFromLiveJobsAsync_K8sClientThrows_ClaimedSetUnchanged()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.TryClaim(Guid.NewGuid()); // claim one so available=1

        var k8sClient = new Mock<IKubernetesJobClient>();
        k8sClient.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("K8s API unavailable"));

        var opts = new DispatchServiceOptions { Namespace = "default", KiroPvcPool = ["pvc-1", "pvc-2"] };

        // Should not propagate the exception
        var act = async () => await pool.RebuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);
        await act.Should().NotThrowAsync();

        pool.AvailableCount.Should().Be(1, "claimed set is unchanged when rebuild fails");
    }

    [Fact]
    public async Task RebuildFromLiveJobsAsync_EmptyPool_ReturnsImmediately()
    {
        var pool = new PvcPool([]);
        var k8sClient = new Mock<IKubernetesJobClient>();

        var opts = new DispatchServiceOptions { Namespace = "default", KiroPvcPool = [] };
        await pool.RebuildFromLiveJobsAsync(k8sClient.Object, opts, CancellationToken.None);

        // No calls to K8s API — TotalCount=0 short-circuits
        k8sClient.Verify(c => c.ListJobsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_ConcurrentClaims_NoDuplicatePvcsReturned()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2", "pvc-3"]);
        var claimed = new System.Collections.Concurrent.ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var pvc = pool.TryClaim(Guid.NewGuid());
            if (pvc is not null)
                claimed.Add(pvc);
        }));

        await Task.WhenAll(tasks);

        claimed.Should().OnlyHaveUniqueItems("concurrent claims must not return the same PVC twice");
        claimed.Count.Should().BeLessThanOrEqualTo(3, "can only claim up to pool size");
    }
}
