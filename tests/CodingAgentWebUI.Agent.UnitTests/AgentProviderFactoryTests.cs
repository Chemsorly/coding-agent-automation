using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentProviderFactory"/>.
/// </summary>
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
    public void CreatePipelineProvider_NullConfig_Throws()
    {
        var factory = CreateFactory();
        var act = () => factory.CreatePipelineProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreatePipelineProvider_UnsupportedType_Throws()
    {
        var factory = CreateFactory();
        var config = CreateProviderConfig(ProviderKind.Pipeline, "Jenkins");

        var act = () => factory.CreatePipelineProvider(config);
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Jenkins*");
    }

    [Fact]
    public void CreatePipelineProvider_GitHub_ValidConfig_ReturnsProvider()
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

        var provider = factory.CreatePipelineProvider(config);
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
    public void CreatePipelineProvider_WithOrchestratorProxy_DoesNotRequireToken()
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

        var provider = factory.CreatePipelineProvider(config);
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
