using AwesomeAssertions;
using CodingAgentWebUI.JobController.Dispatch;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for PvcPool (thread-safe claim/release, exhaustion, counts).
/// </summary>
public sealed class PvcPoolTests
{
    // ── Construction ──────────────────────────────────────────────────────

    [Fact]
    public void TotalCount_ReflectsConstructorPvcs()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2", "pvc-3"]);
        pool.TotalCount.Should().Be(3);
    }

    [Fact]
    public void AvailableCount_InitiallyEqualsTotal()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.AvailableCount.Should().Be(2);
    }

    [Fact]
    public void EmptyPool_TotalAndAvailableAreZero()
    {
        var pool = new PvcPool([]);
        pool.TotalCount.Should().Be(0);
        pool.AvailableCount.Should().Be(0);
    }

    // ── TryClaim ──────────────────────────────────────────────────────────

    [Fact]
    public void TryClaim_ReturnsFirstAvailablePvc()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        var claimed = pool.TryClaim(Guid.NewGuid());
        claimed.Should().Be("pvc-1");
    }

    [Fact]
    public void TryClaim_ReducesAvailableCount()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.TryClaim(Guid.NewGuid());
        pool.AvailableCount.Should().Be(1);
    }

    [Fact]
    public void TryClaim_WhenExhausted_ReturnsNull()
    {
        var pool = new PvcPool(["pvc-1"]);
        pool.TryClaim(Guid.NewGuid()); // claim the only one
        var second = pool.TryClaim(Guid.NewGuid());
        second.Should().BeNull();
    }

    [Fact]
    public void TryClaim_EmptyPool_ReturnsNull()
    {
        var pool = new PvcPool([]);
        pool.TryClaim(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void TryClaim_MultipleCallers_EachGetDifferentPvc()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        var first = pool.TryClaim(Guid.NewGuid());
        var second = pool.TryClaim(Guid.NewGuid());

        first.Should().NotBe(second);
        new[] { first, second }.Should().BeEquivalentTo(["pvc-1", "pvc-2"]);
    }

    // ── Release ───────────────────────────────────────────────────────────

    [Fact]
    public void Release_AfterClaim_RestoresPvcToPool()
    {
        var pool = new PvcPool(["pvc-1"]);
        pool.TryClaim(Guid.NewGuid());
        pool.AvailableCount.Should().Be(0);

        pool.Release("pvc-1");
        pool.AvailableCount.Should().Be(1);
    }

    [Fact]
    public void Release_AllowsReClaimAfterRelease()
    {
        var pool = new PvcPool(["pvc-1"]);
        pool.TryClaim(Guid.NewGuid());
        pool.Release("pvc-1");

        var reclaimed = pool.TryClaim(Guid.NewGuid());
        reclaimed.Should().Be("pvc-1");
    }

    [Fact]
    public void Release_UnknownPvc_DoesNotThrow()
    {
        var pool = new PvcPool(["pvc-1"]);
        var act = () => pool.Release("pvc-unknown");
        act.Should().NotThrow();
    }

    [Fact]
    public void Release_EmptyString_DoesNotThrow()
    {
        var pool = new PvcPool(["pvc-1"]);
        var act = () => pool.Release("");
        act.Should().NotThrow();
    }

    [Fact]
    public void Release_NotPreviouslyClaimed_DoesNotAffectCount()
    {
        var pool = new PvcPool(["pvc-1", "pvc-2"]);
        pool.Release("pvc-1"); // never claimed
        pool.AvailableCount.Should().Be(2);
    }

    // ── Claim-all then release-all ────────────────────────────────────────

    [Fact]
    public void ClaimAll_ThenReleaseAll_RestoresFullAvailability()
    {
        var pvcs = new[] { "pvc-1", "pvc-2", "pvc-3" };
        var pool = new PvcPool(pvcs);
        var claimed = pvcs.Select(_ => pool.TryClaim(Guid.NewGuid())).ToList();

        pool.AvailableCount.Should().Be(0);

        foreach (var pvc in claimed)
            pool.Release(pvc!);

        pool.AvailableCount.Should().Be(3);
    }
}
