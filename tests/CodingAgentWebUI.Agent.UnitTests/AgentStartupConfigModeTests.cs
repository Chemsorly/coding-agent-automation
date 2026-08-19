using AwesomeAssertions;

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

    // ── Fallback / regression guard for absent --mode ─────────────────────────

    [Fact]
    public async Task WhenModeIsAbsent_WithWorkItemId_FallsBackToInference_WorkItemMode()
    {
        using var _ = SetRequiredEnvVars();

        // Regression guard: behavior must be unchanged when --mode is absent.
        // If --work-item-id is present, IsWorkItemMode is true.
        var config = await AgentStartupConfig.ResolveAsync(
            ["--work-item-id=some-id"]);

        config.IsWorkItemMode.Should().BeTrue();
        config.WorkItemId.Should().Be("some-id");
    }

    [Fact]
    public async Task WhenModeIsAbsent_WithoutWorkItemId_FallsBackToInference_ChatMode()
    {
        using var _ = SetRequiredEnvVars();

        // Regression guard: no --mode and no --work-item-id → chat mode (IsWorkItemMode = false).
        var config = await AgentStartupConfig.ResolveAsync([]);

        config.IsWorkItemMode.Should().BeFalse();
    }

    // ── Unknown --mode value ──────────────────────────────────────────────────

    [Fact]
    public async Task WhenModeIsUnknown_FallsBackToInference()
    {
        using var _ = SetRequiredEnvVars();

        // Unknown value → fallback to work-item-id inference (no throw).
        var config = await AgentStartupConfig.ResolveAsync(
            ["--mode=unknown", "--work-item-id=wid-1"]);

        config.IsWorkItemMode.Should().BeTrue("inference: work-item-id present → work-item mode");
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
