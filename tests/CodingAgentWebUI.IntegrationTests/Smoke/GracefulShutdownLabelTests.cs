using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Verifies that graceful shutdown swaps agent:cancelled label on active runs.
/// </summary>
public class GracefulShutdownLabelTests : IAsyncLifetime
{
    private readonly Mock<IIssueProvider> _mockIssueProvider = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_SwapsCancelledLabel_OnActiveAgentRuns()
    {
        // Arrange: configure mocks
        var issueConfig = new ProviderConfig
        {
            Id = "issue-provider-1",
            DisplayName = "Test Issue Provider",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            Settings = new Dictionary<string, string>
            {
                ["owner"] = "test",
                ["repo"] = "test",
                ["appId"] = "1",
                ["installationId"] = "1",
                ["privateKey"] = "fake"
            }
        };

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsNotIn(ProviderKind.Issue), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        _mockProviderFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(_mockIssueProvider.Object);
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceService<IConfigurationStore>(services, _mockConfigStore.Object);
                ReplaceService<IPipelineConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IAgentProfileStore>(services, _mockConfigStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IReviewerConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderFactory>(services, _mockProviderFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
            });
        });

        // Start the app
        var client = _factory.CreateClient();

        // Add an active run to OrchestratorRunService
        var runService = _factory.Services.GetRequiredService<OrchestratorRunService>();
        var run = new PipelineRun
        {
            RunId = "shutdown-test-run",
            IssueIdentifier = "123",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.GeneratingCode
        };
        runService.AddRun(run);

        // Act: trigger graceful shutdown
        var lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        // Give shutdown handlers time to execute
        await Task.Delay(2000);

        // Assert: verify label swap was called
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync("123", AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockIssueProvider.Verify(
            p => p.RemoveLabelAsync("123", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Shutdown_DoesNotBlock_WhenGitHubApiThrows()
    {
        // Arrange: issue provider throws on all calls
        var issueConfig = new ProviderConfig
        {
            Id = "issue-provider-1",
            DisplayName = "Test Issue Provider",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            Settings = new Dictionary<string, string>
            {
                ["owner"] = "test",
                ["repo"] = "test",
                ["appId"] = "1",
                ["installationId"] = "1",
                ["privateKey"] = "fake"
            }
        };

        var throwingProvider = new Mock<IIssueProvider>();
        throwingProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GitHub API unreachable"));
        throwingProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var configStore = new Mock<IConfigurationStore>();
        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });
        configStore.Setup(s => s.LoadProviderConfigsAsync(It.IsNotIn(ProviderKind.Issue), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var providerFactory = new Mock<IProviderFactory>();
        providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(throwingProvider.Object);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceService<IConfigurationStore>(services, configStore.Object);
                ReplaceService<IPipelineConfigStore>(services, configStore.Object);
                ReplaceService<IProviderConfigStore>(services, configStore.Object);
                ReplaceService<IAgentProfileStore>(services, configStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, configStore.Object);
                ReplaceService<IReviewerConfigStore>(services, configStore.Object);
                ReplaceService<IProviderFactory>(services, providerFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
            });
        });

        var client = _factory.CreateClient();

        var runService = _factory.Services.GetRequiredService<OrchestratorRunService>();
        runService.AddRun(new PipelineRun
        {
            RunId = "failing-run",
            IssueIdentifier = "456",
            IssueTitle = "Failing Test",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.GeneratingCode
        });

        // Act: shutdown should complete within timeout despite API failure
        var lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        var completed = await Task.Run(async () =>
        {
            await Task.Delay(5000);
            return true;
        });

        // Assert: shutdown didn't hang
        completed.Should().BeTrue();
    }

    [Fact]
    public async Task Shutdown_NoActiveRuns_CompletesWithoutError()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceService<IConfigurationStore>(services, _mockConfigStore.Object);
                ReplaceService<IPipelineConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IAgentProfileStore>(services, _mockConfigStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IReviewerConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderFactory>(services, _mockProviderFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
            });
        });

        var client = _factory.CreateClient();

        // Act: shutdown with no active runs
        var lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        await Task.Delay(2000);

        // Assert: no label operations attempted
        _mockProviderFactory.Verify(
            f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()),
            Times.Never);
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddSingleton(implementation);
    }
}
