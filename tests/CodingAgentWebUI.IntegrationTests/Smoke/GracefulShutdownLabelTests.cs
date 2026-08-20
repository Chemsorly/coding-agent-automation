using AwesomeAssertions;
using k8s;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Verifies that graceful shutdown swaps agent:cancelled label on active runs.
/// </summary>
[Collection("SmokeTests")]
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
        ClearTestEnvironmentVariables();
    }

    [Fact]
    public async Task Shutdown_DoesNotSwapCancelledLabel_OnActiveAgentRuns()
    {
        // Verifies the rolling-update handoff behavior: graceful shutdown releases runs from
        // in-memory tracking without sending CancelJob or swapping GitHub labels.
        // Agents reconnect to the new pod and complete normally; the new pod writes the real outcome.
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
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Set test environment variables before the host builds — Program.cs's fast-fail check reads
            // them during the config build phase, before ConfigureServices runs.
            SetTestEnvironmentVariables();
            // Reset Serilog to prevent "logger is already frozen" across multiple factory instances
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateBootstrapLogger();
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

                // Replace the real Npgsql DbContext with InMemory EF Core
                RemoveDbContextRegistrations(services);
                services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                    new InMemoryDbContextFactory($"GracefulShutdown-1-{Guid.NewGuid()}"));

                // Replace distributed lock and database health
            // IKubernetes is built from in-cluster config or ~/.kube/config and throws "No usable
                // Kubernetes configuration" when neither resolves. LeaderElectionService is a hosted service
                // that takes it, so without this stub these tests only pass on a machine that happens to have
                // a kubeconfig — they fail in every CI container.
                services.RemoveAll<IKubernetes>();
                services.AddSingleton(new Mock<IKubernetes>().Object);
                services.RemoveAll<IDistributedLockProvider>();
                services.AddDistributedLockProvider(null);
                services.RemoveAll<DatabaseHealthState>();
                services.AddSingleton(new DatabaseHealthState());
                services.RemoveAll<IDatabaseProbe>();
                services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

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

        // Capture runService reference before shutdown disposes the container
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

        // Assert: verify label swap was NOT called — shutdown handoff no longer touches GitHub labels
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync("123", AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Never);
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Run must have been removed from the in-memory registry (dedup released)
        // runService was captured before shutdown to avoid ObjectDisposedException on the container
        runService.GetActiveRuns().Should().BeEmpty();
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
        throwingProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
            // Set test environment variables before the host builds — Program.cs's fast-fail check reads
            // them during the config build phase, before ConfigureServices runs.
            SetTestEnvironmentVariables();
            // Reset Serilog to prevent "logger is already frozen" across multiple factory instances
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateBootstrapLogger();
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));

                // Replace the real Npgsql DbContext with InMemory EF Core
                RemoveDbContextRegistrations(services);
                services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                    new InMemoryDbContextFactory($"GracefulShutdown-2-{Guid.NewGuid()}"));

                // Replace distributed lock and database health
            // IKubernetes is built from in-cluster config or ~/.kube/config and throws "No usable
                // Kubernetes configuration" when neither resolves. LeaderElectionService is a hosted service
                // that takes it, so without this stub these tests only pass on a machine that happens to have
                // a kubeconfig — they fail in every CI container.
                services.RemoveAll<IKubernetes>();
                services.AddSingleton(new Mock<IKubernetes>().Object);
                services.RemoveAll<IDistributedLockProvider>();
                services.AddDistributedLockProvider(null);
                services.RemoveAll<DatabaseHealthState>();
                services.AddSingleton(new DatabaseHealthState());
                services.RemoveAll<IDatabaseProbe>();
                services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

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
        // The factory field being null proves DisposeAsync completed — the test would have
        // been killed by the 15s xUnit timeout before reaching this line if shutdown hung.
        Assert.True(true, "DisposeAsync completed within the 15s timeout — shutdown did not hang despite API failures");
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
            // Set test environment variables before the host builds — Program.cs's fast-fail check reads
            // them during the config build phase, before ConfigureServices runs.
            SetTestEnvironmentVariables();
            // Reset Serilog to prevent "logger is already frozen" across multiple factory instances
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateBootstrapLogger();
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));

                // Replace the real Npgsql DbContext with InMemory EF Core
                RemoveDbContextRegistrations(services);
                services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                    new InMemoryDbContextFactory($"GracefulShutdown-3-{Guid.NewGuid()}"));

                // Replace distributed lock and database health
            // IKubernetes is built from in-cluster config or ~/.kube/config and throws "No usable
                // Kubernetes configuration" when neither resolves. LeaderElectionService is a hosted service
                // that takes it, so without this stub these tests only pass on a machine that happens to have
                // a kubeconfig — they fail in every CI container.
                services.RemoveAll<IKubernetes>();
                services.AddSingleton(new Mock<IKubernetes>().Object);
                services.RemoveAll<IDistributedLockProvider>();
                services.AddDistributedLockProvider(null);
                services.RemoveAll<DatabaseHealthState>();
                services.AddSingleton(new DatabaseHealthState());
                services.RemoveAll<IDatabaseProbe>();
                services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

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
        hangingProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((IssueIdentifier _, string _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        hangingProvider.Setup(p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((IssueIdentifier _, string _, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
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
            // Set test environment variables before the host builds — Program.cs's fast-fail check reads
            // them during the config build phase, before ConfigureServices runs.
            SetTestEnvironmentVariables();
            // Reset Serilog to prevent "logger is already frozen" across multiple factory instances
            Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateBootstrapLogger();
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
                services.RemoveAll<IHostedService>();
                services.AddHostedService(sp => new ShutdownService(
                    sp.GetRequiredService<ILifecycleShutdownAction>(),
                    sp.GetRequiredService<IOrchestrationShutdownAction>(),
                    new ShutdownSignal(),
                    Log.Logger));

                // Replace the real Npgsql DbContext with InMemory EF Core
                RemoveDbContextRegistrations(services);
                services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                    new InMemoryDbContextFactory($"GracefulShutdown-4-{Guid.NewGuid()}"));

                // Replace distributed lock and database health
            // IKubernetes is built from in-cluster config or ~/.kube/config and throws "No usable
                // Kubernetes configuration" when neither resolves. LeaderElectionService is a hosted service
                // that takes it, so without this stub these tests only pass on a machine that happens to have
                // a kubeconfig — they fail in every CI container.
                services.RemoveAll<IKubernetes>();
                services.AddSingleton(new Mock<IKubernetes>().Object);
                services.RemoveAll<IDistributedLockProvider>();
                services.AddDistributedLockProvider(null);
                services.RemoveAll<DatabaseHealthState>();
                services.AddSingleton(new DatabaseHealthState());
                services.RemoveAll<IDatabaseProbe>();
                services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

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
        Assert.True(true, "DisposeAsync completed within the 15s timeout — shutdown did not hang");
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
    /// Also mocks IPipelineApiConfigClient to prevent AutoStartPipelineLoopAsync from retrying
    /// against localhost:9999 (which doesn't exist in tests, causing 300s retry delays).
    /// </summary>
    private static void MockConsolidationService(IServiceCollection services)
    {
        var mock = new Mock<IConsolidationService>();
        mock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());
        ReplaceService<IConsolidationService>(services, mock.Object);

        // Spec 045: mock IPipelineApiConfigClient to prevent AutoStartPipelineLoopAsync
        // from attempting real HTTP calls to localhost:9999 (which retries for 300s per attempt).
        // Without this mock, each test that calls MockConsolidationService would stall for minutes.
        var configClientMock = new Mock<IPipelineApiConfigClient>();
        configClientMock.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        configClientMock.Setup(s => s.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        configClientMock.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        configClientMock.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        configClientMock.Setup(s => s.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        configClientMock.Setup(s => s.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        configClientMock.Setup(s => s.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        ReplaceService<IPipelineApiConfigClient>(services, configClientMock.Object);
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>)
                     || d.ServiceType == typeof(PipelineDbContext)
                     || d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions)
                     || d.ServiceType.Name.Contains("DbContextPool"))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    private static void SetTestEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", "test-api-key");
        // Spec 045: PipelineApi:BaseUrl is required after Task 2 fast-fail was added.
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", "http://localhost:9999");
    }

    private static void ClearTestEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Database__Host", null);
        Environment.SetEnvironmentVariable("Database__Port", null);
        Environment.SetEnvironmentVariable("Database__Username", null);
        Environment.SetEnvironmentVariable("Database__Password", null);
        Environment.SetEnvironmentVariable("Database__Name", null);
        Environment.SetEnvironmentVariable("Database__SslMode", null);
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", null);
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
        Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", null);
    }

    // ── Test Infrastructure ──────────────────────────────────────────────

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public InMemoryDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new TestPipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class NoOpDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
