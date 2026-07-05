using CodingAgentWebUI.Orchestration.Dispatch;
using AwesomeAssertions;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DispatchService's static/internal helper methods:
/// - Deterministic K8s Job naming (Property 8)
/// - Image resolution from AgentSelector
/// - PVC pool availability calculation (Property 12)
/// - Agent type detection (kiro vs opencode)
/// </summary>
public class DispatchServiceLogicTests
{
    // ── Job Naming ──────────────────────────────────────────────────────

    [Fact]
    public void GenerateJobName_UsesFirst8HexCharsOfGuid()
    {
        var id = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
        var name = DispatchService.GenerateJobName(id);
        // ToString("N") = "abcdef0123456789abcdef0123456789" → first 8 = "abcdef01"
        name.Should().Be("caa-abcdef01");
    }

    [Fact]
    public void GenerateJobName_IsDeterministic()
    {
        var id = Guid.NewGuid();
        var name1 = DispatchService.GenerateJobName(id);
        var name2 = DispatchService.GenerateJobName(id);
        name1.Should().Be(name2);
    }

    [Fact]
    public void GenerateJobName_MaxLengthWithinDnsLimit()
    {
        // "caa-" (4) + 8 hex chars = 12 chars total, well within 63 DNS limit
        var id = Guid.NewGuid();
        var name = DispatchService.GenerateJobName(id);
        name.Length.Should().Be(12);
        name.Length.Should().BeLessThanOrEqualTo(63);
    }

    [Fact]
    public void GenerateJobName_StartsWithCaaPrefix()
    {
        var id = Guid.NewGuid();
        var name = DispatchService.GenerateJobName(id);
        name.Should().StartWith("caa-");
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "caa-00000000")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "caa-ffffffff")]
    [InlineData("12345678-1234-1234-1234-123456789abc", "caa-12345678")]
    public void GenerateJobName_KnownValues(string guidStr, string expected)
    {
        var id = Guid.Parse(guidStr);
        DispatchService.GenerateJobName(id).Should().Be(expected);
    }

    // ── Selector Normalization ───────────────────────────────────────────

    [Theory]
    [InlineData("kiro,dotnet,dotnet10", "dotnet,dotnet10,kiro")]
    [InlineData("dotnet10,kiro,dotnet", "dotnet,dotnet10,kiro")]
    [InlineData("opencode,python,python312", "opencode,python,python312")]
    [InlineData("kiro", "kiro")]
    [InlineData("", "")]
    public void NormalizeSelector_SortsLabelsLexicographically(string input, string expected)
    {
        DispatchService.NormalizeSelector(input).Should().Be(expected);
    }

    // ── PVC Pool Availability ───────────────────────────────────────────

    [Fact]
    public void CalculateAvailablePvcs_AllAvailableWhenNoneClaimed()
    {
        var configured = new[] { "pvc-1", "pvc-2", "pvc-3" };
        var claimed = Array.Empty<string>();

        var available = DispatchService.CalculateAvailablePvcs(configured, claimed);

        available.Should().BeEquivalentTo(configured);
    }

    [Fact]
    public void CalculateAvailablePvcs_ExcludesClaimedPvcs()
    {
        var configured = new[] { "pvc-1", "pvc-2", "pvc-3" };
        var claimed = new[] { "pvc-2" };

        var available = DispatchService.CalculateAvailablePvcs(configured, claimed);

        available.Should().BeEquivalentTo(new[] { "pvc-1", "pvc-3" });
    }

    [Fact]
    public void CalculateAvailablePvcs_EmptyWhenAllClaimed()
    {
        var configured = new[] { "pvc-1", "pvc-2" };
        var claimed = new[] { "pvc-1", "pvc-2" };

        var available = DispatchService.CalculateAvailablePvcs(configured, claimed);

        available.Should().BeEmpty();
    }

    [Fact]
    public void CalculateAvailablePvcs_IgnoresClaimedNotInPool()
    {
        var configured = new[] { "pvc-1", "pvc-2" };
        var claimed = new[] { "pvc-99", "pvc-1" };

        var available = DispatchService.CalculateAvailablePvcs(configured, claimed);

        available.Should().BeEquivalentTo(new[] { "pvc-2" });
    }

    [Fact]
    public void CalculateAvailablePvcs_EmptyPoolReturnsEmpty()
    {
        var configured = Array.Empty<string>();
        var claimed = new[] { "pvc-1" };

        var available = DispatchService.CalculateAvailablePvcs(configured, claimed);

        available.Should().BeEmpty();
    }
}
