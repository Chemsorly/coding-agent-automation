using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentProviderFactory"/>. They change <c>KIRO_CLI_PATH</c> and <c>OPENCODE_SERVER_PASSWORD</c>,
/// so the class runs in the shared <c>EnvironmentVariables</c> collection, not in parallel with other tests that do.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentProviderFactoryTests
{
    private static AgentProviderFactory CreateFactory(
        IKiroCliOrchestrator? orchestrator = null,
        IHttpClientFactory? httpClientFactory = null,
        PipelineConfiguration? config = null,
        OrchestratorProxy? orchestratorProxy = null)
    {
        return new AgentProviderFactory(
            orchestrator ?? new Mock<IKiroCliOrchestrator>().Object,
            httpClientFactory ?? new Mock<IHttpClientFactory>().Object,
            config ?? new PipelineConfiguration(),
            orchestratorProxy);
    }

    private static OrchestratorProxy CreateTestProxy()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        return new OrchestratorProxy(connection, "test-job");
    }

    [Fact]
    public void Constructor_NullOrchestrator_Throws()
    {
        var act = () => new AgentProviderFactory(null!, new Mock<IHttpClientFactory>().Object, new PipelineConfiguration());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        var act = () => new AgentProviderFactory(new Mock<IKiroCliOrchestrator>().Object, null!, new PipelineConfiguration());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullConfig_Throws()
    {
        var act = () => new AgentProviderFactory(new Mock<IKiroCliOrchestrator>().Object, new Mock<IHttpClientFactory>().Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResolveCliPath ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveCliPath_VariableIsSet_ReturnsVariableValue()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "/custom/kiro-cli");
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvKiroCliPath, AgentDefaults.KiroCliPath);
            result.Should().Be("/custom/kiro-cli");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origValue);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsUnset_ReturnsDefault()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, null);
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvKiroCliPath, AgentDefaults.KiroCliPath);
            result.Should().Be(AgentDefaults.KiroCliPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origValue);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsEmpty_ReturnsDefault()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "");
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvKiroCliPath, AgentDefaults.KiroCliPath);
            result.Should().Be(AgentDefaults.KiroCliPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origValue);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsWhitespace_ReturnsDefault()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "   ");
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvKiroCliPath, AgentDefaults.KiroCliPath);
            result.Should().Be(AgentDefaults.KiroCliPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origValue);
        }
    }

    [Fact]
    public void ResolveCliPath_ClaudeVariableIsSet_ReturnsVariableValue()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath, "/custom/claude");
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvClaudeCliPath, AgentDefaults.ClaudeCliPath);
            result.Should().Be("/custom/claude");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath, origValue);
        }
    }

    [Fact]
    public void ResolveCliPath_ClaudeVariableIsUnset_ReturnsDefault()
    {
        var origValue = Environment.GetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath, null);
            var result = AgentProviderFactory.ResolveCliPath(AgentDefaults.EnvClaudeCliPath, AgentDefaults.ClaudeCliPath);
            result.Should().Be(AgentDefaults.ClaudeCliPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvClaudeCliPath, origValue);
        }
    }

    [Fact]
    public void Constructor_NullOrchestratorProxy_DoesNotThrow()
    {
        var act = () => new AgentProviderFactory(
            new Mock<IKiroCliOrchestrator>().Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            orchestratorProxy: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void CreateIssueProvider_AlwaysThrows()
    {
        var factory = CreateFactory();
        var config = CreateProviderConfig(ProviderKind.Issue, "GitHub");

        var act = () => factory.CreateIssueProvider(config);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateRepositoryProvider_NullConfig_Throws()
    {
        var factory = CreateFactory();
        var act = () => factory.CreateRepositoryProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateRepositoryProvider_UnsupportedType_Throws()
    {
        var factory = CreateFactory();
        var config = CreateProviderConfig(ProviderKind.Repository, "Bitbucket");

        var act = () => factory.CreateRepositoryProvider(config);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Bitbucket*");
    }

    [Fact]
    public void CreateRepositoryProvider_GitHub_MissingToken_Throws()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                // Missing "token"
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
        };

        var act = () => factory.CreateRepositoryProvider(config);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*token*");
    }

    [Fact]
    public void CreateRepositoryProvider_GitHub_ValidConfig_ReturnsProvider()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "ghs_test_token",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
        };

        var provider = factory.CreateRepositoryProvider(config);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    [Fact]
    public void CreateAgentProvider_NullConfig_Throws()
    {
        var factory = CreateFactory();
        var act = () => factory.CreateAgentProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateAgentProvider_UnsupportedType_Throws()
    {
        var factory = CreateFactory();
        var config = CreateProviderConfig(ProviderKind.Agent, "OpenAI");

        var act = () => factory.CreateAgentProvider(config);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*OpenAI*");
    }

    [Fact]
    public void CreateAgentProvider_KiroCli_ReturnsProvider()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Model] = "auto"
            }
        };

        var provider = factory.CreateAgentProvider(config);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IAgentProvider>();
    }

    [Fact]
    public void CreateAgentProvider_KiroCliWithOnlyAnEffort_GetsItsOwnOrchestrator()
    {
        // The injected orchestrator runs without flags, so an effort needs an orchestrator that passes it.
        var injected = new Mock<IKiroCliOrchestrator>(MockBehavior.Strict);
        var factory = CreateFactory(orchestrator: injected.Object);
        var config = new ProviderConfig
        {
            Id = "agent-kiro-effort",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Kiro with effort",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Model] = "auto",
                [ProviderSettingKeys.Effort] = "max"
            }
        };

        var provider = factory.CreateAgentProvider(config);

        provider.Invoking(p => p.GetHealthStatus()).Should().NotThrow("the strict injected orchestrator is never used");
    }

    private static ProviderConfig AgentConfig(string providerType, params (string Key, string Value)[] settings) => new()
    {
        Id = "agent-light",
        Kind = ProviderKind.Agent,
        ProviderType = providerType,
        DisplayName = "Light tier agent",
        Settings = settings.ToDictionary(s => s.Key, s => s.Value),
    };

    [Fact]
    public void CreateAgentProvider_KiroCli_ReadsTheLightSettings()
    {
        var config = AgentConfig("KiroCli",
            (ProviderSettingKeys.Model, "claude-sonnet-4-6"), (ProviderSettingKeys.Effort, "high"),
            (ProviderSettingKeys.LightModel, "claude-haiku-4-5"), (ProviderSettingKeys.LightEffort, "low"));

        var kiro = CreateFactory().CreateAgentProvider(config)
            .Should().BeOfType<CodingAgent.Agent.KiroCli.KiroCliAgentProvider>().Subject;

        kiro.LightModel.Should().Be("claude-haiku-4-5");
        kiro.LightEffort.Should().Be(AgentEffortLevel.Low);
        kiro.Model.Should().Be("claude-sonnet-4-6");
        kiro.Effort.Should().Be(AgentEffortLevel.High);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAgentProvider_KiroCli_BlankLightSettings_AreUnset(string blank)
    {
        var config = AgentConfig("KiroCli",
            (ProviderSettingKeys.LightModel, blank), (ProviderSettingKeys.LightEffort, blank));

        var kiro = CreateFactory().CreateAgentProvider(config)
            .Should().BeOfType<CodingAgent.Agent.KiroCli.KiroCliAgentProvider>().Subject;

        kiro.LightModel.Should().BeNull();
        kiro.LightEffort.Should().BeNull("an unset light effort falls back to the route's");
    }

    [Fact]
    public void CreateAgentProvider_KiroCli_LightEffortAuto_IsAValue_NotUnset()
    {
        var kiro = CreateFactory().CreateAgentProvider(AgentConfig("KiroCli", (ProviderSettingKeys.LightEffort, "auto")))
            .Should().BeOfType<CodingAgent.Agent.KiroCli.KiroCliAgentProvider>().Subject;

        kiro.LightEffort.Should().Be(AgentEffortLevel.Auto);
    }

    [Fact]
    public void CreateAgentProvider_KiroCli_WithoutLightSettings_HasNoLightTier()
    {
        var kiro = CreateFactory().CreateAgentProvider(AgentConfig("KiroCli", (ProviderSettingKeys.Model, "auto")))
            .Should().BeOfType<CodingAgent.Agent.KiroCli.KiroCliAgentProvider>().Subject;

        kiro.LightModel.Should().BeNull();
        kiro.LightEffort.Should().BeNull();
    }

    [Fact]
    public void CreateAgentProvider_KiroCliWithOnlyLightSettings_KeepsTheInjectedOrchestrator()
    {
        // The light values reach only the orchestrators of isolated calls; the shared one is chosen on the route's own settings.
        var injected = new Mock<IKiroCliOrchestrator>();
        injected.SetupGet(o => o.IsExecuting).Returns(true);
        var config = AgentConfig("KiroCli",
            (ProviderSettingKeys.LightModel, "claude-haiku-4-5"), (ProviderSettingKeys.LightEffort, "low"));

        var provider = CreateFactory(orchestrator: injected.Object).CreateAgentProvider(config);

        provider.GetHealthStatus().IsExecuting.Should().BeTrue("the injected orchestrator is still the shared one");
    }

    [Fact]
    public void CreateAgentProvider_ClaudeCode_ReadsTheLightSettings()
    {
        var config = AgentConfig("claudecode",
            (ProviderSettingKeys.Model, "claude-opus-5-5"), (ProviderSettingKeys.Effort, "xhigh"),
            (ProviderSettingKeys.LightModel, "claude-haiku-4-5"), (ProviderSettingKeys.LightEffort, "low"));

        var claude = CreateFactory().CreateAgentProvider(config)
            .Should().BeOfType<CodingAgent.Agent.ClaudeCode.ClaudeCodeAgentProvider>().Subject;

        claude.LightModel.Should().Be("claude-haiku-4-5");
        claude.LightEffort.Should().Be(AgentEffortLevel.Low);
        claude.Model.Should().Be("claude-opus-5-5");
        claude.Effort.Should().Be(AgentEffortLevel.XHigh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateAgentProvider_ClaudeCode_BlankLightSettings_AreUnset(string blank)
    {
        var config = AgentConfig("claudecode",
            (ProviderSettingKeys.LightModel, blank), (ProviderSettingKeys.LightEffort, blank));

        var claude = CreateFactory().CreateAgentProvider(config)
            .Should().BeOfType<CodingAgent.Agent.ClaudeCode.ClaudeCodeAgentProvider>().Subject;

        claude.LightModel.Should().BeNull();
        claude.LightEffort.Should().BeNull("an unset light effort falls back to the route's");
    }

    [Fact]
    public void CreateAgentProvider_ClaudeCode_LightEffortAuto_IsAValue_NotUnset()
    {
        var claude = CreateFactory().CreateAgentProvider(AgentConfig("claudecode", (ProviderSettingKeys.LightEffort, "auto")))
            .Should().BeOfType<CodingAgent.Agent.ClaudeCode.ClaudeCodeAgentProvider>().Subject;

        claude.LightEffort.Should().Be(AgentEffortLevel.Auto);
    }

    [Fact]
    public void CreateAgentProvider_ClaudeCode_WithoutLightSettings_HasNoLightTier()
    {
        var claude = CreateFactory().CreateAgentProvider(AgentConfig("claudecode", (ProviderSettingKeys.Model, "claude-opus-5-5")))
            .Should().BeOfType<CodingAgent.Agent.ClaudeCode.ClaudeCodeAgentProvider>().Subject;

        claude.LightModel.Should().BeNull();
        claude.LightEffort.Should().BeNull();
    }

    [Theory]
    [InlineData("openai/gpt-5-mini", "openai/gpt-5-mini")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    public void CreateAgentProvider_OpenCode_ReadsTheLightModel(string setting, string? expected)
    {
        var original = Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, "test-password-32-chars-long-enough");
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient { BaseAddress = new Uri(AgentDefaults.OpenCodeBaseUrl) });
            var config = AgentConfig(AgentDefaults.OpenCodeHttpClientName,
                (ProviderSettingKeys.Model, "openai/gpt-5"), (ProviderSettingKeys.LightModel, setting));

            var openCode = CreateFactory(httpClientFactory: httpClientFactory.Object).CreateAgentProvider(config)
                .Should().BeOfType<CodingAgent.Agent.OpenCode.OpenCodeAgentProvider>().Subject;

            openCode.LightModel.Should().Be(expected);
            openCode.Model.Should().Be("openai/gpt-5");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, original);
        }
    }

    [Fact]
    public void CreateAgentProvider_ClaudeCode_ReturnsProviderWithSettings()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "agent-claude",
            Kind = ProviderKind.Agent,
            ProviderType = "claudecode",
            DisplayName = "Claude Code Agent",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Model] = "claude-opus-4-8",
                [ProviderSettingKeys.Effort] = "xhigh",
                [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription,
                [ProviderSettingKeys.McpConfigPath] = "/tmp/mcp.json"
            }
        };

        var provider = factory.CreateAgentProvider(config);

        var claude = provider.Should().BeOfType<CodingAgent.Agent.ClaudeCode.ClaudeCodeAgentProvider>().Subject;
        claude.Model.Should().Be("claude-opus-4-8");
        claude.Effort.Should().Be(AgentEffortLevel.XHigh);
        claude.AuthMode.Should().Be(ClaudeCodeAuthModes.Subscription);
        claude.McpConfigPath.Should().Be("/tmp/mcp.json");
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_NullConfig_Throws()
    {
        var factory = CreateFactory();
        var act = () => factory.CreatePipelineProviderAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_UnsupportedType_Throws()
    {
        var factory = CreateFactory();
        var config = CreateProviderConfig(ProviderKind.Pipeline, "Jenkins");

        var act = () => factory.CreatePipelineProviderAsync(config, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Jenkins*");
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_GitHub_ValidConfig_ReturnsProvider()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "pipeline-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "Test Pipeline",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "ghs_test_token",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo"
            }
        };

        var provider = await factory.CreatePipelineProviderAsync(config, CancellationToken.None);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IPipelineProvider>();
    }

    [Fact]
    public void CreateRepositoryProvider_CaseInsensitiveProviderType()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "github", // lowercase
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "ghs_test",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
        };

        var provider = factory.CreateRepositoryProvider(config);
        provider.Should().NotBeNull();
    }

    [Fact]
    public void CreateRepositoryProvider_WithOrchestratorProxy_DoesNotRequireToken()
    {
        var proxy = CreateTestProxy();
        var factory = CreateFactory(orchestratorProxy: proxy);
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                // No "token" — proxy provides token refresh
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
        };

        var provider = factory.CreateRepositoryProvider(config);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_WithOrchestratorProxy_DoesNotRequireToken()
    {
        var proxy = CreateTestProxy();
        var factory = CreateFactory(orchestratorProxy: proxy);
        var config = new ProviderConfig
        {
            Id = "pipeline-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "Test Pipeline",
            Settings = new Dictionary<string, string>
            {
                // No "token" — proxy provides token refresh
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo"
            }
        };

        var provider = await factory.CreatePipelineProviderAsync(config, CancellationToken.None);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IPipelineProvider>();
    }

    [Fact]
    public void CreateRepositoryProvider_WithoutOrchestratorProxy_RequiresToken()
    {
        var factory = CreateFactory(orchestratorProxy: null);
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                [ProviderSettingKeys.BaseBranch] = "main"
            }
        };

        var act = () => factory.CreateRepositoryProvider(config);
        act.Should().Throw<ArgumentException>().WithMessage("*token*");
    }

    // ── GitLab repository provider ───────────────────────────────────────

    [Fact]
    public void CreateRepositoryProvider_GitLab_ValidConfig_ReturnsProvider()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "repo-gitlab-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.com",
                [ProviderSettingKeys.Token] = "glpat-fake-token",
                [ProviderSettingKeys.ProjectId] = "42"
            }
        };

        var provider = factory.CreateRepositoryProvider(config);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    /// <summary>
    /// Characterization test: ParseProjectId runs BEFORE the orchestratorProxy branch in
    /// CreateGitLabRepositoryProvider. When a proxy is present and projectId is non-numeric,
    /// ArgumentException is thrown before the proxy branch is evaluated.
    /// </summary>
    [Fact]
    public void CreateRepositoryProvider_GitLab_WithProxy_InvalidProjectId_ThrowsArgumentException()
    {
        using var proxy = CreateTestProxy();
        var factory = CreateFactory(orchestratorProxy: proxy);
        var config = new ProviderConfig
        {
            Id = "repo-gitlab-2",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.com",
                [ProviderSettingKeys.ProjectId] = "not-a-number"
            }
        };

        var act = () => factory.CreateRepositoryProvider(config);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*not-a-number*");
    }

    [Fact]
    public void CreateRepositoryProvider_GitLab_NoProxy_InvalidProjectId_ThrowsArgumentException()
    {
        var factory = CreateFactory(orchestratorProxy: null);
        var config = new ProviderConfig
        {
            Id = "repo-gitlab-3",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.com",
                [ProviderSettingKeys.ProjectId] = "not-a-number"
            }
        };

        var act = () => factory.CreateRepositoryProvider(config);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*not-a-number*");
    }

    // ── GitLab pipeline provider ─────────────────────────────────────────

    [Fact]
    public async Task CreatePipelineProviderAsync_GitLab_ValidConfig_ReturnsProvider()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "pipeline-gitlab-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab Pipeline",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.com",
                [ProviderSettingKeys.Token] = "glpat-fake-token",
                [ProviderSettingKeys.ProjectId] = "42"
            }
        };

        var provider = await factory.CreatePipelineProviderAsync(config, CancellationToken.None);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IPipelineProvider>();
    }

    [Fact]
    public async Task CreatePipelineProviderAsync_GitLab_InvalidProjectId_ThrowsArgumentException()
    {
        var factory = CreateFactory();
        var config = new ProviderConfig
        {
            Id = "pipeline-gitlab-2",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab Pipeline",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.com",
                [ProviderSettingKeys.ProjectId] = "not-a-number"
            }
        };

        var act = () => factory.CreatePipelineProviderAsync(config, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not-a-number*");
    }

    private static ProviderConfig CreateProviderConfig(ProviderKind kind, string providerType) => new()
    {
        Id = "test-id",
        Kind = kind,
        ProviderType = providerType,
        DisplayName = "Test",
        Settings = new Dictionary<string, string>()
    };

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
