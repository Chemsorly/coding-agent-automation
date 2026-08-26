using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentRuntimeOptionsSetup"/>.
///
/// Each test sets environment variables, runs <see cref="AgentRuntimeOptionsSetup.Configure"/>,
/// then restores the original value. Collected under the shared
/// <see cref="EnvironmentVariablesCollection"/> to prevent cross-test interference.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class AgentRuntimeOptionsSetupTests
{
    private readonly AgentRuntimeOptionsSetup _setup = new();

    private static void WithEnv(string key, string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(key);
        try
        {
            if (value is null)
                Environment.SetEnvironmentVariable(key, null);
            else
                Environment.SetEnvironmentVariable(key, value);

            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    // ── IsChatMode ────────────────────────────────────────────────────────

    [Fact]
    public void Configure_ChatModeTrue_SetsIsChatModeTrue()
    {
        WithEnv(AgentDefaults.EnvChatMode, "true", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.IsChatMode.Should().BeTrue();
        });
    }

    [Fact]
    public void Configure_ChatModeTrueUpperCase_SetsIsChatModeTrue()
    {
        WithEnv(AgentDefaults.EnvChatMode, "TRUE", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.IsChatMode.Should().BeTrue("comparison is OrdinalIgnoreCase");
        });
    }

    [Fact]
    public void Configure_ChatModeFalse_SetsIsChatModeFalse()
    {
        WithEnv(AgentDefaults.EnvChatMode, "false", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.IsChatMode.Should().BeFalse();
        });
    }

    [Fact]
    public void Configure_ChatModeAbsent_IsChatModeFalse()
    {
        WithEnv(AgentDefaults.EnvChatMode, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.IsChatMode.Should().BeFalse("absent env var must default to false");
        });
    }

    // ── ChatSessionId ─────────────────────────────────────────────────────

    [Fact]
    public void Configure_ChatSessionIdSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvChatSessionId, "session-abc-123", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatSessionId.Should().Be("session-abc-123");
        });
    }

    [Fact]
    public void Configure_ChatSessionIdAbsent_DefaultsToEmpty()
    {
        WithEnv(AgentDefaults.EnvChatSessionId, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatSessionId.Should().Be("");
        });
    }

    // ── AgentLabels ───────────────────────────────────────────────────────

    [Fact]
    public void Configure_AgentLabelsSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvAgentLabels, "kiro,dotnet,dotnet10", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.AgentLabels.Should().Be("kiro,dotnet,dotnet10");
        });
    }

    [Fact]
    public void Configure_AgentLabelsAbsent_DefaultsToEmpty()
    {
        WithEnv(AgentDefaults.EnvAgentLabels, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.AgentLabels.Should().Be("");
        });
    }

    // ── ChatModel ─────────────────────────────────────────────────────────

    [Fact]
    public void Configure_ChatModelSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvChatModel, "claude-sonnet-4-5", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatModel.Should().Be("claude-sonnet-4-5");
        });
    }

    [Fact]
    public void Configure_ChatModelAbsent_IsNull()
    {
        WithEnv(AgentDefaults.EnvChatModel, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatModel.Should().BeNull("absent optional env var must remain null");
        });
    }

    // ── ChatEffort ────────────────────────────────────────────────────────

    [Fact]
    public void Configure_ChatEffortSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvChatEffort, "high", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatEffort.Should().Be("high");
        });
    }

    [Fact]
    public void Configure_ChatEffortAbsent_IsNull()
    {
        WithEnv(AgentDefaults.EnvChatEffort, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.ChatEffort.Should().BeNull();
        });
    }

    // ── AgentProviderType ─────────────────────────────────────────────────

    [Fact]
    public void Configure_AgentProviderTypeSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvAgentProviderType, "OpenCode", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.AgentProviderType.Should().Be("OpenCode");
        });
    }

    [Fact]
    public void Configure_AgentProviderTypeAbsent_DefaultsToEmpty()
    {
        WithEnv(AgentDefaults.EnvAgentProviderType, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.AgentProviderType.Should().Be("");
        });
    }

    // ── OpenCodeBaseUrl ───────────────────────────────────────────────────

    [Fact]
    public void Configure_OpenCodeBaseUrlSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvOpenCodeBaseUrl, "http://localhost:9000", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.OpenCodeBaseUrl.Should().Be("http://localhost:9000");
        });
    }

    [Fact]
    public void Configure_OpenCodeBaseUrlAbsent_IsNull()
    {
        WithEnv(AgentDefaults.EnvOpenCodeBaseUrl, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.OpenCodeBaseUrl.Should().BeNull();
        });
    }

    // ── OpenCodeServerPassword ────────────────────────────────────────────

    [Fact]
    public void Configure_OpenCodeServerPasswordSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvOpenCodeServerPassword, "s3cr3t", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.OpenCodeServerPassword.Should().Be("s3cr3t");
        });
    }

    [Fact]
    public void Configure_OpenCodeServerPasswordAbsent_IsNull()
    {
        WithEnv(AgentDefaults.EnvOpenCodeServerPassword, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.OpenCodeServerPassword.Should().BeNull();
        });
    }

    // ── KiroCliPath ───────────────────────────────────────────────────────

    [Fact]
    public void Configure_KiroCliPathSet_PropagatesValue()
    {
        WithEnv(AgentDefaults.EnvKiroCliPath, "/usr/local/bin/kiro-cli", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.KiroCliPath.Should().Be("/usr/local/bin/kiro-cli");
        });
    }

    [Fact]
    public void Configure_KiroCliPathAbsent_IsNull()
    {
        WithEnv(AgentDefaults.EnvKiroCliPath, null, () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.KiroCliPath.Should().BeNull("absent optional KiroCliPath must not set a value");
        });
    }

    [Fact]
    public void Configure_KiroCliPathEmpty_IsNull()
    {
        // Empty string is treated the same as absent — !string.IsNullOrEmpty check
        WithEnv(AgentDefaults.EnvKiroCliPath, "", () =>
        {
            var opts = new AgentRuntimeOptions();
            _setup.Configure(opts);
            opts.KiroCliPath.Should().BeNull("empty KiroCliPath must not override the null default");
        });
    }
}
