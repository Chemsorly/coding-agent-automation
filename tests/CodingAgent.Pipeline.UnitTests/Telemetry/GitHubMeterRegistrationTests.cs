using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Verifies that the <c>CodingAgent.GitHub</c> meter is registered in all four long-lived
/// non-agent processes, and is NOT registered in the agent (which must not emit these metrics).
///
/// Uses the <c>FindSourceFile</c> pattern from <see cref="QualityGateMetricsMeterRegistrationTests"/>.
/// </summary>
public class GitHubMeterRegistrationTests
{
    private static string FindSourceFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            throw new InvalidOperationException("Could not find solution root");
        return Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void MeterName_ConstantHasExpectedValue()
    {
        GitHubTelemetry.MeterName.Should().Be("CodingAgent.GitHub",
            "the meter name constant must match the value registered in each host Program.cs");
    }

    [Fact]
    public void ApiProgramCs_RegistersGitHubMeter()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Api/Program.cs"));

        source.Should().Contain("GitHubTelemetry.MeterName",
            "Api/Program.cs must register the CodingAgent.GitHub meter so that github.api.requests " +
            "and github.rate_limit.remaining are exported from the API process");
    }

    [Fact]
    public void SchedulerProgramCs_RegistersGitHubMeter()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Scheduler/Program.cs"));

        source.Should().Contain("GitHubTelemetry.MeterName",
            "Scheduler/Program.cs must register the CodingAgent.GitHub meter");
    }

    [Fact]
    public void WebOpenTelemetryRegistration_RegistersGitHubMeter()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Web/OpenTelemetryRegistration.cs"));

        source.Should().Contain("GitHubTelemetry.MeterName",
            "Web/OpenTelemetryRegistration.cs must register the CodingAgent.GitHub meter");
    }

    [Fact]
    public void JobControllerProgramCs_RegistersGitHubMeter()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.JobController/Program.cs"));

        source.Should().Contain("GitHubTelemetry.MeterName",
            "JobController/Program.cs must register the CodingAgent.GitHub meter — it is a long-lived " +
            "non-agent process that makes GitHub API calls");
    }

    [Fact]
    public void AgentProgramCs_DoesNotRegisterGitHubMeter()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Agent/Program.cs"));

        source.Should().NotContain("GitHubTelemetry.MeterName",
            "Agent/Program.cs must NOT register the CodingAgent.GitHub meter — " +
            "agent pods must not emit GitHub API metrics per issue #2967");

        // Also verify the literal string is absent (defense against registering via the literal instead of constant)
        source.Should().NotContain(GitHubTelemetry.MeterName,
            "Agent/Program.cs must not contain the literal meter name either");
    }

    [Fact]
    public void ApiProgramCs_CallsGitHubTelemetryPreInitialize()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Api/Program.cs"));

        source.Should().Contain("GitHubTelemetry.PreInitialize()",
            "Api/Program.cs must call GitHubTelemetry.PreInitialize() to seed github.api.requests " +
            "counter tag combinations so Prometheus increase() works on first increment");
    }

    [Fact]
    public void SchedulerProgramCs_CallsGitHubTelemetryPreInitialize()
    {
        var source = File.ReadAllText(FindSourceFile("src/CodingAgent.Scheduler/Program.cs"));

        source.Should().Contain("GitHubTelemetry.PreInitialize()",
            "Scheduler/Program.cs must call GitHubTelemetry.PreInitialize()");
    }

    // TODO: Add PreInitialize() coverage for JobController/Program.cs and Web/Program.cs.
    // Both processes call GitHubTelemetry.PreInitialize() (confirmed in the diff), but the
    // test suite only asserts this for Api and Scheduler. A future removal from JobController
    // or Web would not be caught. Add:
    //   JobControllerProgramCs_CallsGitHubTelemetryPreInitialize
    //   WebProgramCs_CallsGitHubTelemetryPreInitialize
    // mirroring the pattern above.
}
