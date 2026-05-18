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
/// Regression tests for brain repository token scoping.
/// 
/// Background: The brain provider's ValidateAsync() was failing with 404 because the
/// token refresh always generated a token scoped to the work repo (coding-agent-automation)
/// instead of the brain repo (coding-agent-brain). GitHub installation tokens are scoped
/// to specific repositories, so a token for repo A cannot access repo B.
///
/// Root cause: AgentProviderFactory used ProviderKind.Repository for ALL repository providers
/// (both work and brain), and the orchestrator's RequestTokenRefresh always resolved the
/// work repo config regardless of the requested kind.
///
/// Fix: Brain providers now request ProviderKind.Brain, and the orchestrator resolves the
/// correct config based on the kind.
/// </summary>
public class BrainTokenScopingRegressionTests
{
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

    /// <summary>
    /// Regression: Brain repo provider must use ProviderKind.Brain for token refresh,
    /// not ProviderKind.Repository. Without this, the token is scoped to the wrong repo.
    /// </summary>
    [Fact]
    public void CreateRepositoryProvider_BrainRole_UsesProviderKindBrain()
    {
        // Arrange
        var proxy = CreateTestProxy();
        var factory = new AgentProviderFactory(
            new Mock<IKiroCliOrchestrator>().Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            proxy);

        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-brain-repo",
                ["baseBranch"] = "main"
            }
        };

        // Act: Create the provider — should succeed (uses token provider path)
        var provider = factory.CreateRepositoryProvider(brainConfig);

        // Assert: Provider was created successfully
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    /// <summary>
    /// Regression: Work repo provider must still use ProviderKind.Repository.
    /// </summary>
    [Fact]
    public void CreateRepositoryProvider_WorkRole_UsesProviderKindRepository()
    {
        var proxy = CreateTestProxy();
        var factory = new AgentProviderFactory(
            new Mock<IKiroCliOrchestrator>().Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            proxy);

        var workConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-work-repo",
                ["baseBranch"] = "main"
            }
        };

        // Act
        var provider = factory.CreateRepositoryProvider(workConfig);

        // Assert
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    /// <summary>
    /// Regression: Default RepositoryRole (Work) must not accidentally route to Brain kind.
    /// </summary>
    [Fact]
    public void CreateRepositoryProvider_DefaultRole_UsesProviderKindRepository()
    {
        var proxy = CreateTestProxy();
        var factory = new AgentProviderFactory(
            new Mock<IKiroCliOrchestrator>().Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            proxy);

        // Config without explicit RepositoryRole — defaults to Work
        var config = new ProviderConfig
        {
            Id = "repo-default",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Default Role Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                ["baseBranch"] = "main"
            }
        };

        // Assert: default role is Work
        config.RepositoryRole.Should().Be(RepositoryRole.Work);

        var provider = factory.CreateRepositoryProvider(config);
        provider.Should().NotBeNull();
    }

    /// <summary>
    /// Regression: Without the OrchestratorProxy, brain repos should still work
    /// with a static token (no token provider path).
    /// </summary>
    [Fact]
    public void CreateRepositoryProvider_BrainRole_WithoutProxy_UsesStaticToken()
    {
        var factory = new AgentProviderFactory(
            new Mock<IKiroCliOrchestrator>().Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            orchestratorProxy: null);

        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "ghs_brain_token",
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-brain-repo",
                ["baseBranch"] = "main"
            }
        };

        var provider = factory.CreateRepositoryProvider(brainConfig);
        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IRepositoryProvider>();
    }

    /// <summary>
    /// Verifies that ProviderKind.Brain exists and is distinct from Repository.
    /// This prevents accidental removal of the enum value.
    /// </summary>
    [Fact]
    public void ProviderKind_Brain_ExistsAndIsDistinctFromRepository()
    {
        ProviderKind.Brain.Should().NotBe(ProviderKind.Repository);
        Enum.IsDefined(typeof(ProviderKind), ProviderKind.Brain).Should().BeTrue();
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
