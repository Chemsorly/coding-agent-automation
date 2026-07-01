using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

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
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                ["appId"] = "1",
                [ProviderSettingKeys.InstallationId] = "1",
                ["privateKey"] = "fake"
            }
        };

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsNotIn(ProviderKind.Issue), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        _mockProviderFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(_mockIssueProvider.Object);
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Host", "");
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                // Re-add ShutdownService so graceful shutdown fires through IHostedLifecycleService
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));
                ReplaceService<IConfigurationStore>(services, _mockConfigStore.Object);
                ReplaceService<IPipelineConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IAgentProfileStore>(services, _mockConfigStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IReviewerConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderFactory>(services, _mockProviderFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
                MockConsolidationService(services);
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
            CurrentStep = PipelineStep.GeneratingCode,
            AgentId = "test-agent-1"
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

    [Fact(Timeout = 15_000)]
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
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                ["appId"] = "1",
                [ProviderSettingKeys.InstallationId] = "1",
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
        configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = configStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        var providerFactory = new Mock<IProviderFactory>();
        providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(throwingProvider.Object);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Host", "");
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));
                ReplaceService<IConfigurationStore>(services, configStore.Object);
                ReplaceService<IPipelineConfigStore>(services, configStore.Object);
                ReplaceService<IProviderConfigStore>(services, configStore.Object);
                ReplaceService<IAgentProfileStore>(services, configStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, configStore.Object);
                ReplaceService<IReviewerConfigStore>(services, configStore.Object);
                ReplaceService<IProviderFactory>(services, providerFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
                MockConsolidationService(services);
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

        // Assert: DisposeAsync completes (host's 5s ShutdownTimeout proceeds despite exceptions).
        // If shutdown hangs, the xUnit 15s timeout will kill this test.
        await _factory.DisposeAsync();
        _factory = null;
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
            builder.UseSetting("Database:Host", "");
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));
                ReplaceService<IConfigurationStore>(services, _mockConfigStore.Object);
                ReplaceService<IPipelineConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IAgentProfileStore>(services, _mockConfigStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IReviewerConfigStore>(services, _mockConfigStore.Object);
                ReplaceService<IProviderFactory>(services, _mockProviderFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
                MockConsolidationService(services);
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

    [Fact(Timeout = 15_000)]
    public async Task Shutdown_DoesNotBlock_WhenProviderHangs()
    {
        // Arrange: issue provider hangs indefinitely (simulates network timeout)
        var issueConfig = new ProviderConfig
        {
            Id = "issue-provider-1",
            DisplayName = "Test Issue Provider",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "test",
                ["appId"] = "1",
                [ProviderSettingKeys.InstallationId] = "1",
                ["privateKey"] = "fake"
            }
        };

        var hangingProvider = new Mock<IIssueProvider>();
        hangingProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        hangingProvider.Setup(p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        hangingProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var configStore = new Mock<IConfigurationStore>();
        configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { issueConfig });
        configStore.Setup(s => s.LoadProviderConfigsAsync(It.IsNotIn(ProviderKind.Issue), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = configStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        var providerFactory = new Mock<IProviderFactory>();
        providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(hangingProvider.Object);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Host", "");
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));
                ReplaceService<IConfigurationStore>(services, configStore.Object);
                ReplaceService<IPipelineConfigStore>(services, configStore.Object);
                ReplaceService<IProviderConfigStore>(services, configStore.Object);
                ReplaceService<IAgentProfileStore>(services, configStore.Object);
                ReplaceService<IQualityGateConfigStore>(services, configStore.Object);
                ReplaceService<IReviewerConfigStore>(services, configStore.Object);
                ReplaceService<IProviderFactory>(services, providerFactory.Object);
                ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);
                MockConsolidationService(services);
            });
        });

        var client = _factory.CreateClient();

        var runService = _factory.Services.GetRequiredService<OrchestratorRunService>();
        runService.AddRun(new PipelineRun
        {
            RunId = "hanging-run",
            IssueIdentifier = "789",
            IssueTitle = "Hanging Test",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-1",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.GeneratingCode
        });

        // Act: shutdown should complete within timeout despite provider hanging
        var lifetime = _factory.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        // Assert: DisposeAsync completes (host's 5s ShutdownTimeout aborts the hanging callback).
        // If shutdown hangs, the xUnit 15s timeout will kill this test.
        await _factory.DisposeAsync();
        _factory = null;
    }

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddSingleton(implementation);
    }

    /// <summary>
    /// Adds IConsolidationService mock to prevent Program.cs startup from hitting PostgreSQL.
    /// </summary>
    private static void MockConsolidationService(IServiceCollection services)
    {
        var mock = new Mock<IConsolidationService>();
        mock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        ReplaceService<IConsolidationService>(services, mock.Object);
    }
}
