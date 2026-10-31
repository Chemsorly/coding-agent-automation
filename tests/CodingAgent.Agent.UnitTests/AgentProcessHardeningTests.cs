using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using KiroCliLib.Core;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentProcessHardening"/>.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentProcessHardeningTests
{
    // ── RemoveSecretsFromEnvironment ─────────────────────────────────────────

    [Fact]
    public void RemoveSecretsFromEnvironment_RemovesTheKeyAndTheDiagnosticsSetting()
    {
        var savedKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        var savedDiag = Environment.GetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, "canary-key");
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, "0");

            AgentProcessHardening.RemoveSecretsFromEnvironment();

            Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey).Should().BeNull();
            Environment.GetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, savedKey);
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, savedDiag);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterRemoval_AChildProcessDoesNotInheritEither(bool touchEnvironment)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var savedKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        var savedDiag = Environment.GetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, "canary-key");
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, "0");

            AgentProcessHardening.RemoveSecretsFromEnvironment();

            var psi = new ProcessStartInfo("/usr/bin/env")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            if (touchEnvironment)
                ChildProcessEnvironment.StripTelemetry(psi);

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            output.Should().NotContain("AGENT_API_KEY=");
            output.Should().NotContain("DOTNET_EnableDiagnostics=");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, savedKey);
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, savedDiag);
        }
    }

    [Fact]
    public void AfterRemoval_ConfigurationDoesNotHaveTheKey()
    {
        var savedKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        var savedDiag = Environment.GetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, "canary-key");

            AgentProcessHardening.RemoveSecretsFromEnvironment();

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            configuration[AgentDefaults.EnvAgentApiKey].Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, savedKey);
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, savedDiag);
        }
    }

    [Fact]
    public async Task ResolveAsync_StillReturnsTheKey_AfterWhichRemovalDoesNotChangeTheConfig()
    {
        var savedKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        var savedUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl);
        var savedDiag = Environment.GetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, "canary-key");
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl, "http://localhost:5000");

            var config = await AgentStartupConfig.ResolveAsync(["--mode=chat"]);

            AgentProcessHardening.RemoveSecretsFromEnvironment();

            config.AgentApiKey.Should().Be("canary-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, savedKey);
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl, savedUrl);
            Environment.SetEnvironmentVariable(AgentProcessHardening.EnvDotnetEnableDiagnostics, savedDiag);
        }
    }

    [Fact]
    public void MarkProcessNotDumpable_OnOtherSystems_DoesNothing()
    {
        // Never call MarkProcessNotDumpable on Linux in a test: prctl(PR_SET_DUMPABLE, 0)
        // marks the test runner process itself as non-dumpable for the rest of the run,
        // which breaks coverage tooling and crash analysis.
        if (OperatingSystem.IsLinux())
            return;

        AgentProcessHardening.MarkProcessNotDumpable().Should().BeTrue();
    }
}
