using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="InitiatedByConstants"/> helpers:
/// <see cref="InitiatedByConstants.IsManual"/> and
/// <see cref="InitiatedByConstants.ToDisplayString"/>.
/// </summary>
public class InitiatedByConstantsTests
{
    // ── IsManual ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsManual_ManualValue_ReturnsTrue()
    {
        InitiatedByConstants.IsManual(InitiatedByConstants.Manual).Should().BeTrue();
    }

    [Fact]
    public void IsManual_ManualSubvalue_ReturnsTrue()
    {
        // Any "manual:*" sub-value should also be elevated (future extensibility).
        InitiatedByConstants.IsManual("manual:something").Should().BeTrue();
    }

    [Fact]
    public void IsManual_NullValue_ReturnsFalse()
    {
        InitiatedByConstants.IsManual(null).Should().BeFalse();
    }

    [Fact]
    public void IsManual_LoopIssue_ReturnsFalse()
    {
        InitiatedByConstants.IsManual(InitiatedByConstants.LoopIssue).Should().BeFalse();
    }

    [Fact]
    public void IsManual_ConsolidationManual_ReturnsFalse()
    {
        // "consolidation:manual" starts with "consolidation", not "manual" — must NOT be elevated.
        InitiatedByConstants.IsManual(InitiatedByConstants.ConsolidationManual).Should().BeFalse(
            "consolidation:manual starts with 'consolidation', not 'manual', so it must not receive manual priority weight");
    }

    [Fact]
    public void IsManual_EmptyString_ReturnsFalse()
    {
        InitiatedByConstants.IsManual("").Should().BeFalse();
    }

    [Fact]
    public void IsManual_Rehydrated_ReturnsFalse()
    {
        InitiatedByConstants.IsManual(InitiatedByConstants.Rehydrated).Should().BeFalse();
    }

    // ── ToDisplayString ───────────────────────────────────────────────────────

    [Fact]
    public void ToDisplayString_RunModeNew_ReturnsInitiatedByUnchanged()
    {
        var result = InitiatedByConstants.ToDisplayString(InitiatedByConstants.LoopIssue, RunMode.New);
        result.Should().Be(InitiatedByConstants.LoopIssue);
    }

    [Fact]
    public void ToDisplayString_RunModeRework_AppendsReworkSuffix()
    {
        var result = InitiatedByConstants.ToDisplayString(InitiatedByConstants.Manual, RunMode.Rework);
        result.Should().Be("manual (rework)");
    }

    [Fact]
    public void ToDisplayString_RunModeRetry_AppendsRetrySuffix()
    {
        var result = InitiatedByConstants.ToDisplayString(InitiatedByConstants.LoopIssue, RunMode.Retry);
        result.Should().Be("loop:issue (retry)");
    }
}
