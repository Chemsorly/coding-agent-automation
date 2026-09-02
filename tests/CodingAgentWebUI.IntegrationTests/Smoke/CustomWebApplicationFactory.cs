using k8s;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
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
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the app with an
/// InMemory EF Core database and mocked external services. Previously used to force
/// Legacy work-distribution mode; now targets the single K8s-only mode after Spec 041.
///
/// Consuming suites: <see cref="AppStartupTests"/>, <see cref="DiContainerTests"/>,
/// <see cref="PageSmokeTests"/> — all mode-agnostic and unchanged by Spec 041.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"CustomFactory-{Guid.NewGuid()}";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clean up process-global env vars to prevent cross-test pollution
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

        base.Dispose(disposing);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set Database__Host before the host builds — Program.cs's fast-fail check reads it
        // during the config build phase, before ConfigureServices runs.
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
        // Integration tests do not start a real API; use a stub URL to satisfy the check.
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", "http://localhost:9999");

        // Reset Serilog's global logger to a fresh bootstrap state.
        // This prevents "The logger is already frozen" when multiple WebApplicationFactory
        // instances are created in the same process — each Build() call freezes the
        // ReloadableLogger, so we need a new one for each factory invocation.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        builder.ConfigureServices(services =>
        {
            // Reduce shutdown timeout to prevent test host hangs
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // Remove all hosted services (PipelineLoopService, HeartbeatMonitorService,
            // JobQueueDrainService) — they are not needed for integration tests and their
            // background loops can prevent the test host from shutting down cleanly.
            services.RemoveAll<IHostedService>();

            // Replace the real Npgsql DbContext with InMemory EF Core
            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory(_dbName));
            // IKubernetes is built from in-cluster config or ~/.kube/config and throws "No usable
            // Kubernetes configuration" when neither resolves. LeaderElectionService is a hosted service
            // that takes it, so without this stub these tests only pass on a machine that happens to have
            // a kubeconfig — they fail in every CI container.
            services.RemoveAll<IKubernetes>();
            services.AddSingleton(new Mock<IKubernetes>().Object);

            // Replace the distributed lock provider with InProcess (real one uses Postgres advisory locks)
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // Replace DatabaseHealthState with a pre-healthy instance
            services.RemoveAll<DatabaseHealthState>();
            services.AddSingleton(new DatabaseHealthState());

            // Register a no-op IDatabaseProbe so DatabaseStartupService skips real SQL connectivity check
            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Replace IConfigurationStore with a mock returning defaults
            var configStore = CreateConfigurationStoreMock();
            ReplaceService<IConfigurationStore>(services, configStore);
            ReplaceService<IPipelineConfigStore>(services, configStore);
            ReplaceService<IProviderConfigStore>(services, configStore);
            ReplaceService<IAgentProfileStore>(services, configStore);
            ReplaceService<IQualityGateConfigStore>(services, configStore);
            ReplaceService<IReviewerConfigStore>(services, configStore);
            ReplaceService<IProjectStore>(services, configStore);

            // Replace IProviderFactory with a mock
            ReplaceService<IProviderFactory>(services, new Mock<IProviderFactory>().Object);

            // Replace IQualityGateValidator with a mock (prevents real dotnet build/test)
            ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);

            // Replace IConsolidationService — Program.cs calls CleanupOrphanedRunsAsync
            // and RehydrateQueuedRunsAsync during startup, which hit the database directly
            // (not via a hosted service), so RemoveAll<IHostedService> doesn't prevent it.
            var consolidationMock = new Mock<IConsolidationService>();
            consolidationMock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            consolidationMock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ConsolidationRun>());
            ReplaceService<IConsolidationService>(services, consolidationMock.Object);

            // Spec 045: IDispatchOrchestrationService was removed from monolith DI (Task 8), but
            // IssueDrawerService, PrReviewDrawerService, and EpicDrawerService still depend on it.
            // Register a mock to satisfy DI validation in integration tests.
            var dispatchOrchestrationMock = new Mock<IDispatchOrchestrationService>();
            services.AddSingleton(dispatchOrchestrationMock.Object);

            // Replace IPipelineApiWorkItemClient with a mock to prevent real HTTP calls.
            var workItemClientMock = new Mock<IPipelineApiWorkItemClient>();
            workItemClientMock.Setup(s => s.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<PendingWorkItemDto>)Array.Empty<PendingWorkItemDto>());
            services.RemoveAll<IPipelineApiWorkItemClient>();
            services.AddSingleton(workItemClientMock.Object);

            // Spec 045: IPipelineApiConfigClient is registered by AddPipelineApiClient() and points
            // to a stub URL. Replace it with a mock to prevent real HTTP calls during startup.
            var configClientMock = new Mock<IPipelineApiConfigClient>();
            configClientMock.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration());
            configClientMock.Setup(s => s.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ProviderConfig>());
            configClientMock.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<PipelineProject>());
            configClientMock.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
            services.RemoveAll<IPipelineApiConfigClient>();
            services.AddSingleton(configClientMock.Object);

            // Spec 045: IAgentHubConnection is scoped (one per Blazor circuit). Replace with a mock
            // to prevent real SignalR connection attempts to localhost:9999/hubs/agent in tests.
            services.RemoveAll<IAgentHubConnection>();
            services.AddScoped<IAgentHubConnection>(_ => new Mock<IAgentHubConnection>().Object);

            // Spec 045: IPipelineApiRunHistoryClient is registered by AddPipelineApiClient() and would
            // try to connect to localhost:9999. Replace with a mock to prevent real HTTP calls.
            var runHistoryMock = new Mock<IPipelineApiRunHistoryClient>();
            runHistoryMock.Setup(s => s.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = Array.Empty<PipelineRunSummary>(), Page = 1, PageSize = 50, HasMore = false });
            runHistoryMock.Setup(s => s.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PipelineRunSummary?)null);
            services.RemoveAll<IPipelineApiRunHistoryClient>();
            services.AddSingleton(runHistoryMock.Object);
        });
    }

    private static IConfigurationStore CreateConfigurationStoreMock()
    {
        var mock = new Mock<IConfigurationStore>();
        mock.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mock.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        mock.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        mock.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        return mock.Object;
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

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);

        services.AddSingleton(implementation);
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
