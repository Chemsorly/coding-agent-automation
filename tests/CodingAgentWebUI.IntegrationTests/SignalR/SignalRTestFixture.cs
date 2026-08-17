using System.Security.Cryptography;
using System.Text;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.IntegrationTests.SignalR;

/// <summary>
/// WebApplicationFactory that starts Kestrel on a random port with external providers mocked out.
/// Used for SignalR reconnection integration tests.
/// </summary>
public sealed class SignalRTestFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "signalr-test-key";

    public SignalRTestFactory()
    {
        // Start real Kestrel on a random port (required for SignalR client connections)
        UseKestrel(0);
    }

    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

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
        Environment.SetEnvironmentVariable("AGENT_API_KEY", TestApiKey);

        // Reset Serilog to prevent "logger is already frozen" across multiple factory instances.
        // Use SilentLogger so Program.cs's CreateBootstrapLogger() creates a fresh ReloadableLogger.
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().CreateLogger();

        builder.ConfigureServices(services =>
        {
            // Override the API key via options so hub auth uses the test key.
            services.PostConfigure<AgentApiKeyAuthOptions>(
                AgentApiKeyDefaults.AuthenticationScheme,
                opts => opts.ApiKey = TestApiKey);
            // Remove all hosted services
            services.RemoveAll<IHostedService>();

            // Replace the real Npgsql DbContext with InMemory EF Core
            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new InMemoryDbContextFactory($"SignalRTest-{Guid.NewGuid()}"));

            // Replace the distributed lock provider with InProcess (real one uses Postgres advisory locks)
            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            // Replace DatabaseHealthState with a pre-healthy instance
            services.RemoveAll<DatabaseHealthState>();
            services.AddSingleton(new DatabaseHealthState());

            // Replace IDatabaseProbe with no-op so DatabaseStartupService skips SQL connectivity check
            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpDatabaseProbe());

            // Replace external providers with mocks
            var configStore = new Mock<IConfigurationStore>();
            configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PipelineConfiguration());
            configStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ProviderConfig>());
            configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ProviderConfig?)null);

            ReplaceService<IConfigurationStore>(services, configStore.Object);
            ReplaceService<IPipelineConfigStore>(services, configStore.Object);
            ReplaceService<IProviderConfigStore>(services, configStore.Object);
            ReplaceService<IAgentProfileStore>(services, configStore.Object);
            ReplaceService<IQualityGateConfigStore>(services, configStore.Object);
            ReplaceService<IReviewerConfigStore>(services, configStore.Object);
            ReplaceService<IProjectStore>(services, configStore.Object);
            ReplaceService<IProviderFactory>(services, new Mock<IProviderFactory>().Object);
            ReplaceService<IQualityGateValidator>(services, new Mock<IQualityGateValidator>().Object);

            // Mock IConsolidationService — Program.cs calls CleanupOrphanedRunsAsync
            // and RehydrateQueuedRunsAsync at startup which hit PostgreSQL directly.
            var consolidationMock = new Mock<IConsolidationService>();
            consolidationMock.Setup(s => s.CleanupOrphanedRunsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            consolidationMock.Setup(s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<ConsolidationRun>());
            ReplaceService<IConsolidationService>(services, consolidationMock.Object);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
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
        }

        base.Dispose(disposing);
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

    private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors)
            services.Remove(descriptor);
        services.AddSingleton(implementation);
    }
}

/// <summary>
/// Shared fixture for SignalR reconnection integration tests.
/// </summary>
public sealed class SignalRTestFixture : IAsyncLifetime
{
    public SignalRTestFactory Factory { get; } = new();
    public AgentRegistryService Registry { get; private set; } = null!;
    public string ServerAddress => Factory.ServerAddress;

    public async Task InitializeAsync()
    {
        // Trigger host start
        using var _ = Factory.CreateClient();
        Registry = Factory.Services.GetRequiredService<AgentRegistryService>();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
    }

    /// <summary>
    /// Creates a new SignalR hub connection for the given agentId.
    /// </summary>
    public HubConnection CreateHubConnection(string agentId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SignalRTestFactory.TestApiKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId));
        var derivedToken = Convert.ToHexString(hash).ToLowerInvariant();

        return new HubConnectionBuilder()
            .WithUrl($"{ServerAddress}{HubRoutes.Agent}?agentId={agentId}&access_token={derivedToken}")
            .Build();
    }

    /// <summary>
    /// Waits for the registry to reflect the expected status for an agent.
    /// </summary>
    public async Task WaitForStatusAsync(string agentId, AgentStatus expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var entry = Registry.GetByAgentId(agentId);
            if (entry?.Status == expected)
                return;
            await Task.Delay(50);
        }

        var actual = Registry.GetByAgentId(agentId)?.Status;
        throw new TimeoutException(
            $"Agent '{agentId}' did not reach status '{expected}' within timeout. Current: '{actual}'");
    }
}
