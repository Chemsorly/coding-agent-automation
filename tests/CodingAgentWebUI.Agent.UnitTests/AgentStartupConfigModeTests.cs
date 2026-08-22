using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentStartupConfig.ResolveAsync"/> --mode flag parsing.
/// Spec 043 Task 13b.2 / 13b.3.
/// </summary>
public class AgentStartupConfigModeTests
{
    // ── Setup: AGENT_API_KEY and ORCHESTRATOR_URL are required by ResolveAsync.
    // We inject them via environment variables within each test to avoid polluting the test host.

    private static IDisposable SetRequiredEnvVars(string? agentApiKey = "test-key", string? orchestratorUrl = "http://localhost:5000")
    {
        Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, agentApiKey);
        Environment.SetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl, orchestratorUrl);
        return new EnvVarCleanup(AgentDefaults.EnvAgentApiKey, AgentDefaults.EnvOrchestratorUrl);
    }

    // ── Tests for --mode=workitem without --work-item-id ─────────────────────

    [Fact]
    public async Task WhenModeIsWorkitemAndWorkItemIdAbsent_ShouldThrow()
    {
        using var _ = SetRequiredEnvVars();

        // --mode=workitem without --work-item-id must throw InvalidOperationException
        // naming both flags in the message.
        var act = async () => await AgentStartupConfig.ResolveAsync(["--mode=workitem"]);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.And.Message.Should().Contain("--mode=workitem");
        ex.And.Message.Should().Contain("--work-item-id");
    }

    [Fact]
    public async Task WhenModeIsWorkitemAndWorkItemIdPresent_IsWorkItemModeIsTrue()
    {
        using var _ = SetRequiredEnvVars();

        var config = await AgentStartupConfig.ResolveAsync(
            ["--mode=workitem", "--work-item-id=abc-123"]);

        config.IsWorkItemMode.Should().BeTrue();
        config.WorkItemId.Should().Be("abc-123");
    }

    // ── Tests for --mode=chat ─────────────────────────────────────────────────

    [Fact]
    public async Task WhenModeIsChat_IsWorkItemModeIsFalse()
    {
        using var _ = SetRequiredEnvVars();

        var config = await AgentStartupConfig.ResolveAsync(["--mode=chat"]);

        config.IsWorkItemMode.Should().BeFalse();
    }

    [Fact]
    public async Task WhenModeIsChat_CaseSensitivityIgnored()
    {
        using var _ = SetRequiredEnvVars();

        var config = await AgentStartupConfig.ResolveAsync(["--mode=CHAT"]);

        config.IsWorkItemMode.Should().BeFalse();
    }

    // ── Tests for absent --mode and unknown --mode → now throw ───────────────

    [Fact]
    public async Task ResolveAsync_NoMode_Throws()
    {
        using var _ = SetRequiredEnvVars();

        var act = async () => await AgentStartupConfig.ResolveAsync([]);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.And.Message.Should().Contain("--mode is required");
        ex.And.Message.Should().Contain("workitem");
        ex.And.Message.Should().Contain("chat");
    }

    [Fact]
    public async Task ResolveAsync_UnknownMode_Throws()
    {
        using var _ = SetRequiredEnvVars();

        var act = async () => await AgentStartupConfig.ResolveAsync(["--mode=unknown"]);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.And.Message.Should().Contain("unknown");
        ex.And.Message.Should().Contain("workitem");
        ex.And.Message.Should().Contain("chat");
    }

    [Fact]
    public async Task ResolveAsync_EmptyModeValue_Throws()
    {
        using var _ = SetRequiredEnvVars();

        // "--mode=" produces modeArg="" (not null) — routes to the unknown-value throw path
        var act = async () => await AgentStartupConfig.ResolveAsync(["--mode="]);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.And.Message.Should().Contain("workitem");
        ex.And.Message.Should().Contain("chat");
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private sealed class EnvVarCleanup : IDisposable
    {
        private readonly string[] _keys;
        public EnvVarCleanup(params string[] keys) => _keys = keys;
        public void Dispose()
        {
            foreach (var key in _keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }
}
