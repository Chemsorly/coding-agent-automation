using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>The usage threshold of the Claude subscription: readings, the breach that pauses, and the resume override.</summary>
public class CredentialUsageRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 33, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = new(2026, 11, 19, 20, 20, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenDayReset = new(2026, 11, 26, 13, 0, 0, TimeSpan.Zero);
    private static readonly CredentialUsageThresholds Thresholds = CredentialUsageThresholds.Default;

    private static AgentRateLimitObservation Reading(
        string window, double? utilization, DateTimeOffset? resetsAt, string status = "allowed") =>
        new() { Provider = "claude", Window = window, Status = status, Utilization = utilization, ResetsAt = resetsAt };

    private static CredentialUsageState Recorded(params AgentRateLimitObservation[] observations) =>
        CredentialUsageRules.Record(CredentialUsageState.Empty, observations, Now);

    [Fact]
    public void Record_ShouldKeepTheLastReadingPerThresholdWindow_AndIgnoreOverageAndReadingsWithoutUsage()
    {
        var state = CredentialUsageRules.Record(
            Recorded(Reading(CredentialUsageWindows.FiveHour, 0.60, FiveHourReset)),
            [
                Reading(CredentialUsageWindows.FiveHour, 0.66, FiveHourReset),
                Reading(CredentialUsageWindows.SevenDay, 0.21, SevenDayReset),
                Reading("overage", 0.5, null, "rejected"),
                Reading("five_hour", null, FiveHourReset),
            ],
            Now);

        state.Windows.Keys.Should().BeEquivalentTo(CredentialUsageWindows.FiveHour, CredentialUsageWindows.SevenDay);
        state.Windows[CredentialUsageWindows.FiveHour].Should().Be(
            new CredentialUsageReading(0.66, FiveHourReset, "allowed", Now));
    }

    [Fact]
    public void Record_WhenALateReadingOfTheSameWindowIsLower_ShouldKeepTheHigherOne()
    {
        var state = CredentialUsageRules.Record(
            Recorded(Reading(CredentialUsageWindows.FiveHour, 0.80, FiveHourReset)),
            [Reading(CredentialUsageWindows.FiveHour, 0.70, FiveHourReset)], Now.AddMinutes(5));

        state.Windows[CredentialUsageWindows.FiveHour].Utilization.Should().Be(0.80);
    }

    [Fact]
    public void Record_WhenAReadingIsFromAnEarlierWindow_ShouldIgnoreIt_AndANewWindowReplacesTheOld()
    {
        var current = Recorded(Reading(CredentialUsageWindows.FiveHour, 0.10, FiveHourReset));

        CredentialUsageRules.Record(current, [Reading(CredentialUsageWindows.FiveHour, 0.95, FiveHourReset.AddHours(-5))], Now)
            .Windows[CredentialUsageWindows.FiveHour].Utilization.Should().Be(0.10);
        CredentialUsageRules.Record(current, [Reading(CredentialUsageWindows.FiveHour, 0.02, FiveHourReset.AddHours(5))], Now)
            .Windows[CredentialUsageWindows.FiveHour].Utilization.Should().Be(0.02);
    }

    [Fact]
    public void Breach_APerModelWeeklyWindow_ShouldUseTheSevenDayThreshold()
    {
        var breach = CredentialUsageRules.Breach(Recorded(Reading("seven_day_opus", 0.91, SevenDayReset)), Thresholds, Now);

        breach!.Window.Should().Be("seven_day_opus");
        breach.ThresholdPercent.Should().Be(90);
        breach.Detail.Should().Be("Claude 7-day window (opus) at 91 % (threshold 90 %)");
    }

    [Theory]
    [InlineData(0.74, false)]
    [InlineData(0.75, true)]
    [InlineData(0.92, true)]
    public void Breach_WhenTheFiveHourReadingReachesItsThreshold_ShouldPauseUntilTheWindowResets(double utilization, bool breached)
    {
        var breach = CredentialUsageRules.Breach(
            Recorded(Reading(CredentialUsageWindows.FiveHour, utilization, FiveHourReset)), Thresholds, Now);

        if (!breached)
        {
            breach.Should().BeNull();
            return;
        }
        breach.Should().NotBeNull();
        breach!.Window.Should().Be(CredentialUsageWindows.FiveHour);
        breach.ResetsAt.Should().Be(FiveHourReset);
        breach.ThresholdPercent.Should().Be(75);
    }

    [Fact]
    public void Breach_Detail_ShouldNameTheWindowTheUsageAndTheThreshold() =>
        CredentialUsageRules.Breach(Recorded(Reading(CredentialUsageWindows.FiveHour, 0.755, FiveHourReset)), Thresholds, Now)!
            .Detail.Should().Be("Claude 5-hour window at 76 % (threshold 75 %)");

    [Fact]
    public void Breach_WhenAThresholdIsOff_ShouldNeverPauseForThatWindow() =>
        CredentialUsageRules.Breach(
            Recorded(Reading(CredentialUsageWindows.FiveHour, 1.0, FiveHourReset)),
            Thresholds with { FiveHourPercent = 0 }, Now).Should().BeNull();

    [Fact]
    public void Breach_WhenTheReadingHasNoResetTime_ShouldNotPause() =>
        CredentialUsageRules.Breach(Recorded(Reading(CredentialUsageWindows.FiveHour, 0.9, null)), Thresholds, Now)
            .Should().BeNull();

    [Fact]
    public void Breach_WhenTheWindowHasResetSinceTheReading_ShouldNotPause() =>
        CredentialUsageRules.Breach(
            Recorded(Reading(CredentialUsageWindows.FiveHour, 0.9, FiveHourReset)), Thresholds, FiveHourReset)
            .Should().BeNull();

    [Fact]
    public void Breach_WhenBothWindowsAreOver_ShouldPauseUntilTheLaterReset()
    {
        var breach = CredentialUsageRules.Breach(
            Recorded(
                Reading(CredentialUsageWindows.FiveHour, 0.8, FiveHourReset),
                Reading(CredentialUsageWindows.SevenDay, 0.95, SevenDayReset)),
            Thresholds, Now);

        breach!.Window.Should().Be(CredentialUsageWindows.SevenDay);
        breach.ResetsAt.Should().Be(SevenDayReset);
    }

    [Fact]
    public void Resume_ShouldLetTheBreachedWindowThroughUntilItResets_ThenTheThresholdAppliesAgain()
    {
        var resumed = CredentialUsageRules.Resume(
            Recorded(Reading(CredentialUsageWindows.FiveHour, 0.8, FiveHourReset)), Thresholds, Now);

        var later = CredentialUsageRules.Record(resumed, [Reading(CredentialUsageWindows.FiveHour, 0.95, FiveHourReset)], Now);
        CredentialUsageRules.Breach(later, Thresholds, Now).Should().BeNull();

        var nextReset = FiveHourReset.AddHours(5);
        var nextWindow = CredentialUsageRules.Record(
            later, [Reading(CredentialUsageWindows.FiveHour, 0.76, nextReset)], FiveHourReset.AddMinutes(1));
        nextWindow.ResumedThrough.Should().BeEmpty();
        CredentialUsageRules.Breach(nextWindow, Thresholds, FiveHourReset.AddMinutes(1))!.ResetsAt.Should().Be(nextReset);
    }

    [Fact]
    public void Resume_ShouldKeepTheThresholdOfAWindowStillUnderIt()
    {
        var resumed = CredentialUsageRules.Resume(
            Recorded(
                Reading(CredentialUsageWindows.FiveHour, 0.8, FiveHourReset),
                Reading(CredentialUsageWindows.SevenDay, 0.4, SevenDayReset)),
            Thresholds, Now);

        resumed.ResumedThrough.Keys.Should().BeEquivalentTo([CredentialUsageWindows.FiveHour]);
        var climbed = CredentialUsageRules.Record(resumed, [Reading(CredentialUsageWindows.SevenDay, 0.9, SevenDayReset)], Now);
        CredentialUsageRules.Breach(climbed, Thresholds, Now)!.Window.Should().Be(CredentialUsageWindows.SevenDay);
    }

    [Fact]
    public void RateLimitReadingReport_ShouldCrossTheHubWithItsReadings()
    {
        var report = new PipelineRunEventReport
        {
            Kind = PipelineRunEventKind.RateLimitReading,
            RateLimits = [Reading(CredentialUsageWindows.FiveHour, 0.76, FiveHourReset)],
        };

        var bytes = MessagePack.MessagePackSerializer.Serialize(report, AgentHubMessagePack.SerializerOptions);
        var read = MessagePack.MessagePackSerializer.Deserialize<PipelineRunEventReport>(bytes, AgentHubMessagePack.SerializerOptions);

        read.Kind.Should().Be(PipelineRunEventKind.RateLimitReading);
        read.RateLimits.Should().BeEquivalentTo(report.RateLimits);
    }

    [Theory]
    [InlineData(-1, 75, 1)]
    [InlineData(101, 75, 1)]
    [InlineData(75, 101, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(100, 100, 0)]
    public void Validate_ShouldAcceptZeroToAHundredPercent(int fiveHour, int sevenDay, int errors) =>
        new CredentialUsageThresholds { FiveHourPercent = fiveHour, SevenDayPercent = sevenDay }.Validate()
            .Should().HaveCount(errors);
}
