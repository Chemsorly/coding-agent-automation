using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ByteBudget"/>.
/// Verifies thread-safe byte reservation, limit enforcement, and Add/TotalBytes tracking.
/// </summary>
public class ByteBudgetTests
{
    // ── TryReserve — happy path ────────────────────────────────────────

    [Fact]
    public void TryReserve_FirstReservationWithinLimit_ReturnsTrue()
    {
        var budget = new ByteBudget();
        budget.TryReserve(100, maxTotal: 500).Should().BeTrue();
    }

    [Fact]
    public void TryReserve_ExactlyAtLimit_ReturnsTrue()
    {
        var budget = new ByteBudget();
        budget.TryReserve(500, maxTotal: 500).Should().BeTrue();
    }

    [Fact]
    public void TryReserve_AccumulatesTotal()
    {
        var budget = new ByteBudget();
        budget.TryReserve(100, maxTotal: 500);
        budget.TryReserve(200, maxTotal: 500);
        budget.TotalBytes.Should().Be(300);
    }

    // ── TryReserve — limit enforcement ────────────────────────────────

    [Fact]
    public void TryReserve_WouldExceedLimit_ReturnsFalse()
    {
        var budget = new ByteBudget();
        budget.TryReserve(400, maxTotal: 500); // 400 used
        budget.TryReserve(101, maxTotal: 500).Should().BeFalse(); // 400+101 > 500
    }

    [Fact]
    public void TryReserve_WouldExceedLimit_DoesNotModifyTotal()
    {
        var budget = new ByteBudget();
        budget.TryReserve(400, maxTotal: 500);
        budget.TryReserve(200, maxTotal: 500); // rejected

        budget.TotalBytes.Should().Be(400);
    }

    [Fact]
    public void TryReserve_ZeroBytes_AlwaysSucceeds()
    {
        var budget = new ByteBudget();
        budget.TryReserve(0, maxTotal: 0).Should().BeTrue();
        budget.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void TryReserve_AfterExceeding_SubsequentSmallReservationSucceeds()
    {
        var budget = new ByteBudget();
        budget.TryReserve(400, maxTotal: 500); // 400 used
        budget.TryReserve(200, maxTotal: 500); // rejected — 400+200 > 500
        budget.TryReserve(50, maxTotal: 500).Should().BeTrue();  // 400+50 = 450 ≤ 500
        budget.TotalBytes.Should().Be(450);
    }

    // ── Add ───────────────────────────────────────────────────────────

    [Fact]
    public void Add_IncrementsTotalBytes()
    {
        var budget = new ByteBudget();
        budget.Add(100);
        budget.Add(250);
        budget.TotalBytes.Should().Be(350);
    }

    [Fact]
    public void Add_DoesNotEnforceLimits_AllowsUnboundedAccumulation()
    {
        // Add bypasses limit checks — it's a raw counter increment used
        // for bookkeeping after successful reservations
        var budget = new ByteBudget();
        budget.Add(1_000_000);
        budget.TotalBytes.Should().Be(1_000_000);
    }

    // ── Concurrency ───────────────────────────────────────────────────

    [Fact]
    public async Task TryReserve_ConcurrentReservations_TotalNeverExceedsLimit()
    {
        const long maxTotal = 1_000;
        const long chunkSize = 100;
        const int threads = 20;

        var budget = new ByteBudget();
        var tasks = Enumerable.Range(0, threads)
            .Select(_ => Task.Run(() => budget.TryReserve(chunkSize, maxTotal)));

        await Task.WhenAll(tasks);

        budget.TotalBytes.Should().BeLessThanOrEqualTo(maxTotal);
    }
}
