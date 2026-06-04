using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class ApplyQuarantineFilterTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllQuarantined_ReturnsFullList_NoSafetyValve()
    {
        var quarantine = new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.Test1", Reason = "flaky", QuarantinedAt = Now.AddDays(-5) },
                new QuarantinedTest { TestName = "Ns.Test2", Reason = "flaky", QuarantinedAt = Now.AddDays(-3) }
            ]
        };

        var result = QualityGateValidator.ApplyQuarantineFilter(["Ns.Test1", "Ns.Test2"], quarantine, null, Now);

        result.SafetyValveTriggered.Should().BeFalse();
        result.QuarantinedTestNames.Should().BeEquivalentTo(["Ns.Test1", "Ns.Test2"]);
    }

    [Fact]
    public void Mixed_ReturnsOnlyMatchingEntries()
    {
        var quarantine = new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.Flaky", Reason = "flaky", QuarantinedAt = Now.AddDays(-5) }
            ]
        };

        var result = QualityGateValidator.ApplyQuarantineFilter(["Ns.Flaky", "Ns.Real"], quarantine, null, Now);

        result.SafetyValveTriggered.Should().BeFalse();
        result.QuarantinedTestNames.Should().ContainSingle().Which.Should().Be("Ns.Flaky");
    }

    [Fact]
    public void ExpiredEntries_Excluded()
    {
        var quarantine = new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest
                {
                    TestName = "Ns.Expired",
                    Reason = "flaky",
                    QuarantinedAt = Now.AddDays(-60),
                    ExpiresAt = Now.AddDays(-1)
                }
            ]
        };

        var result = QualityGateValidator.ApplyQuarantineFilter(["Ns.Expired"], quarantine, null, Now);

        result.SafetyValveTriggered.Should().BeFalse();
        result.QuarantinedTestNames.Should().BeEmpty();
    }

    [Fact]
    public void SafetyValveTriggered_FlagSet()
    {
        var quarantine = new TestQuarantineConfiguration
        {
            Enabled = true,
            MaxQuarantinedFailuresPerRun = 2,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.F1", Reason = "flaky", QuarantinedAt = Now.AddDays(-1) },
                new QuarantinedTest { TestName = "Ns.F2", Reason = "flaky", QuarantinedAt = Now.AddDays(-1) },
                new QuarantinedTest { TestName = "Ns.F3", Reason = "flaky", QuarantinedAt = Now.AddDays(-1) }
            ]
        };

        var result = QualityGateValidator.ApplyQuarantineFilter(["Ns.F1", "Ns.F2", "Ns.F3"], quarantine, null, Now);

        result.SafetyValveTriggered.Should().BeTrue();
    }

    [Theory]
    [InlineData("src/Services/MyService.cs", "MyService.cs", true)]
    [InlineData("src/Services/MyService.cs", "Service.cs", false)]
    [InlineData("src/Services/MyService.cs", "Services/MyService.cs", true)]
    [InlineData("src\\Services\\MyService.cs", "Services/MyService.cs", true)]
    [InlineData("MyService.cs", "MyService.cs", true)]
    [InlineData("src/IService.cs", "Service.cs", false)]
    public void IsPathSuffixMatch_Cases(string fullPath, string suffix, bool expected)
    {
        QualityGateValidator.IsPathSuffixMatch(fullPath, suffix).Should().Be(expected);
    }
}
