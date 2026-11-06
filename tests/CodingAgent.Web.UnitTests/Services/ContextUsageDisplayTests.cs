using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

public class ContextUsageDisplayTests
{
    private static PhaseUsage Phase(long? peak, long? window) =>
        new(1000, null) { PeakContextTokens = peak, ContextWindowTokens = window };

    [Fact]
    public void PhaseText_WhenPeakAndWindowKnown_ShouldShowBothAndTheShare()
    {
        ContextUsageDisplay.PhaseText(Phase(142_000, 200_000)).Should().Be("142.0K / 200.0K (71%)");
    }

    [Fact]
    public void PhaseText_WhenOnlyPeakKnown_ShouldShowThePeak()
    {
        ContextUsageDisplay.PhaseText(Phase(142_000, null)).Should().Be("142.0K");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 200_000L)]
    [InlineData(0L, 200_000L)]
    public void PhaseText_WhenNoPeak_ShouldShowADash(long? peak, long? window)
    {
        ContextUsageDisplay.PhaseText(Phase(peak, window)).Should().Be("—");
    }

    [Fact]
    public void PhaseText_WhenPeakAboveWindow_ShouldShowTheRealShare()
    {
        ContextUsageDisplay.PhaseText(Phase(210_000, 200_000)).Should().Be("210.0K / 200.0K (105%)");
    }

    [Fact]
    public void Fullest_WhenNoPhaseHasAPeak_ShouldBeNull()
    {
        var phases = new Dictionary<string, PhaseUsage> { ["analysis"] = Phase(null, null) };

        ContextUsageDisplay.Fullest(phases).Should().BeNull();
        ContextUsageDisplay.Fullest(null).Should().BeNull();
    }

    [Fact]
    public void Fullest_ShouldPickTheHighestShareOfTheWindow_NotTheLargestPeak()
    {
        var phases = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = Phase(300_000, 1_000_000),   // 30 %
            ["codegen"] = Phase(150_000, 200_000),      // 75 %
        };

        ContextUsageDisplay.Fullest(phases)!.Value.Phase.Should().Be("codegen");
    }

    [Fact]
    public void Fullest_WithoutWindows_ShouldPickTheLargestPeak()
    {
        var phases = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = Phase(90_000, null),
            ["codegen"] = Phase(120_000, null),
        };

        ContextUsageDisplay.Fullest(phases)!.Value.Phase.Should().Be("codegen");
    }

    [Fact]
    public void Fullest_ShouldPreferAPhaseWithAWindowOverOneWithout()
    {
        var phases = new Dictionary<string, PhaseUsage>
        {
            ["analysis"] = Phase(500_000, null),
            ["codegen"] = Phase(50_000, 200_000),
        };

        ContextUsageDisplay.Fullest(phases)!.Value.Phase.Should().Be("codegen");
    }
}
