using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for the agent provider contract models: <see cref="AgentHealthStatus"/>,
/// <see cref="AgentResult"/> and <see cref="AgentRequest"/>.
/// </summary>
public class AgentProviderContractTests
{
    [Theory]
    [InlineData(false, null, null, "Idle")]
    [InlineData(true, 1234, true, "Running (PID 1234)")]
    [InlineData(true, 5678, false, "Process exited (PID 5678)")]
    [InlineData(true, null, null, "Executing (process state unknown)")]
    public void AgentHealthStatus_Summary_ReturnsExpectedText(
        bool isExecuting, int? pid, bool? isAlive, string expected)
    {
        var status = new AgentHealthStatus
        {
            IsExecuting = isExecuting,
            ProcessId = pid,
            IsProcessAlive = isAlive
        };
        status.Summary.Should().Be(expected);
    }

    [Fact]
    public void AgentResult_Success_ReflectsExitCode()
    {
        new AgentResult { ExitCode = 0, OutputLines = [] }.Success.Should().BeTrue();
        new AgentResult { ExitCode = ExitCodes.GeneralFailure, OutputLines = [] }.Success.Should().BeFalse();
    }

    [Fact]
    public void AgentRequest_ResumeSessionId_DefaultsToNull()
    {
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws" };
        request.ResumeSessionId.Should().BeNull();
        request.UseResume.Should().BeFalse();
    }

    [Fact]
    public void AgentRequest_EnvironmentVariables_DefaultsToNull()
    {
        // Verifies the new EnvironmentVariables property (issue #1913) defaults to null.
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws" };
        request.EnvironmentVariables.Should().BeNull();
    }

    [Fact]
    public void AgentRequest_EnvironmentVariables_CanBeSet()
    {
        // Verifies that EnvironmentVariables can be populated and read back correctly.
        var envVars = new Dictionary<string, string>
        {
            ["SECRET_KEY"] = "secret-value-for-test",
            ["OTHER_KEY"] = "other-value"
        };

        var request = new AgentRequest
        {
            Prompt = "test",
            WorkspacePath = "/ws",
            EnvironmentVariables = envVars
        };

        request.EnvironmentVariables.Should().NotBeNull();
        request.EnvironmentVariables.Should().ContainKey("SECRET_KEY").WhoseValue.Should().Be("secret-value-for-test");
        request.EnvironmentVariables.Should().HaveCount(2);
    }
}
