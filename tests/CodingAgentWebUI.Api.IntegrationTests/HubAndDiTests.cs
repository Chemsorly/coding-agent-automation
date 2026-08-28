using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Tests for health endpoints, authentication, SignalR hub connectivity,
/// DI wiring, API client resolution, and startup failure modes.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class HubAndDiTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HubAndDiTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Health ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_Returns200_Anonymous()
    {
        var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readyz_Returns200_Anonymous()
    {
        var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/readyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PendingEndpoint_WithoutToken_Returns401()
    {
        var anonClient = _factory.CreateClient();
        var response = await anonClient.GetAsync("/api/work-items/pending");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Hub: SignalR connectivity ─────────────────────────────────────────────────

    [Fact]
    public async Task Hub_ValidMasterKeyToken_Connects_AndRegisterAgent_Succeeds()
    {
        // The API hub needs Kestrel running on a real port for SignalR client to connect.
        // We create a separate factory that uses UseKestrel(0) (random port), following the
        // SignalRTestFactory pattern from CodingAgentWebUI.IntegrationTests.
        await using var kestrelFactory = new ApiKestrelFactory();
        using var client = kestrelFactory.CreateClient();  // triggers host start

        var serverAddress = kestrelFactory.ServerAddress;
        var agentId = $"test-agent-{Guid.NewGuid():N}";

        // Derive a per-agent key from the master key (matches AgentApiKeyAuthHandler logic)
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedToken = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        var connection = new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}?agentId={agentId}&access_token={derivedToken}")
            .Build();

        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            connection.State.Should().Be(HubConnectionState.Connected);

            // RegisterAgent should succeed without HubException
            var message = new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "test-host",
                Labels = ["dotnet"]
            };
            Func<Task> act = () => connection.InvokeAsync("RegisterAgent", message);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Hub_WithoutToken_ConnectionIsRejected()
    {
        await using var kestrelFactory = new ApiKestrelFactory();
        using var client = kestrelFactory.CreateClient(); // triggers host start

        var serverAddress = kestrelFactory.ServerAddress;

        // No access_token, no agentId — should be rejected
        var connection = new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}")
            .Build();

        try
        {
            Func<Task> startTask = () => connection.StartAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            await startTask.Should().ThrowAsync<Exception>(
                "connecting to the hub without a token must fail");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    // ── DI: AgentHubFacade wiring ──────────────────────────────────────────────────

    [Fact]
    public void AddApiOrchestration_ResolvesIAgentHubFacade_WithNonNullDbFactory()
    {
        // Mirror of AgentHubFacadeDbFactoryWiringTests — verifies the API's DI wiring
        // doesn't regress the _dbFactory null bug that broke LastProgressAt persistence.
        var facade = _factory.Services.GetRequiredService<IAgentHubFacade>();

        var dbFactoryField = typeof(AgentHubFacade).GetField("_dbFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        dbFactoryField.Should().NotBeNull("AgentHubFacade must have a _dbFactory field");

        var dbFactoryValue = dbFactoryField!.GetValue(facade);
        dbFactoryValue.Should().NotBeNull(
            "AgentHubFacade._dbFactory must not be null in the API container — " +
            "TouchLastProgressAsync requires it to persist heartbeat progress to the DB");
    }

    // ── Client: AddPipelineApiClient resolves every typed client ──────────────────

    [Fact]
    public void AddPipelineApiClient_ResolvesEveryTypedClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddPipelineApiClient(new PipelineApiClientOptions
        {
            BaseUrl = "http://localhost:8080",
            AgentApiKey = "test-key"
        });

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPipelineApiWorkItemClient>().Should().NotBeNull();
        provider.GetRequiredService<IPipelineApiRunHistoryClient>().Should().NotBeNull();
        provider.GetRequiredService<IPipelineApiConfigClient>().Should().NotBeNull();
        provider.GetRequiredService<IPipelineApiHealthClient>().Should().NotBeNull();
        // Without this one the monolith cannot resolve ApiAgentRegistryService and the host fails
        // to start, so it belongs in the same guard as the rest.
        provider.GetRequiredService<IPipelineApiAgentClient>().Should().NotBeNull();
    }

    // ── Startup: missing env vars cause fast-fail ─────────────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> with the console redirected, and returns what was written.
    ///
    /// <c>Program.cs</c> reports a missing setting with <c>Log.Fatal(...)</c> and then returns, so
    /// <c>WebApplicationFactory</c> surfaces only "The entry point exited without ever building an
    /// IHost" — identical whichever guard fired. Without the log text these two tests would both
    /// pass on either guard. A Serilog sink cannot be used to capture it: <c>Program.cs</c>
    /// assigns its own bootstrap logger before the checks run, discarding anything the test
    /// installed. Its console sink writes to <c>Console.Out</c> as it resolves at that moment,
    /// which is why redirecting the console does work.
    /// </summary>
    private static string CaptureConsole(Action action)
    {
        var captured = new StringWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return captured.ToString();
    }

    [Fact]
    public void Startup_MissingDatabaseHost_FailsFastWithNamedSetting()
    {
        // WebApplicationFactory reads env vars during WebApplication.CreateBuilder, before any
        // ConfigureWebHost callback runs, so the sentinel has to be set in the factory's
        // constructor. MissingDbHostFactory does that.
        using var factory = new MissingDbHostFactory();

        // Two things must hold, and the old version of this test asserted neither: the host must
        // not come up, and it must be *this* guard that stopped it.
        Exception? ex = null;
        var console = CaptureConsole(() => ex = Record.Exception(() => factory.CreateClient()));

        ex.Should().NotBeNull("startup must fail when Database__Host is unset");
        FlattenMessages(ex!).Should().Contain("without ever building an IHost");
        console.Should().Contain("Database__Host");
    }

    [Fact]
    public void Startup_MissingAgentApiKey_FailsFastWithNamedSetting()
    {
        using var factory = new MissingAgentApiKeyFactory();

        Exception? ex = null;
        var console = CaptureConsole(() => ex = Record.Exception(() => factory.CreateClient()));

        ex.Should().NotBeNull("startup must fail when AGENT_API_KEY is unset");
        FlattenMessages(ex!).Should().Contain("without ever building an IHost");
        console.Should().Contain("AGENT_API_KEY");
    }

    /// <summary>
    /// Host startup failures surface wrapped — the useful text is usually on an inner exception,
    /// so match against the whole chain rather than the outermost message.
    /// </summary>
    private static string FlattenMessages(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
            sb.AppendLine(current.Message);
        return sb.ToString();
    }
}

/// <summary>
/// Factory that starts Kestrel on a random port (required for SignalR client connections).
/// Follows the SignalRTestFactory pattern from CodingAgentWebUI.IntegrationTests.
/// </summary>
internal sealed class ApiKestrelFactory : WebApplicationFactory<Program>
{
    public ApiKestrelFactory()
    {
        UseKestrel(0);  // random port
    }

    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", ApiWebApplicationFactory.ApiKey);

        // Silence the logger to avoid frozen-logger errors across multiple factory instances
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            services.RemoveAll<IHostedService>();

            RemoveDbContextRegistrations(services);
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new ApiKestrelDbContextFactory($"ApiKestrel-{Guid.NewGuid():N}"));

            services.RemoveAll<IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            services.RemoveAll<IDatabaseProbe>();
            services.AddSingleton<IDatabaseProbe>(new NoOpKestrelDatabaseProbe());

            services.RemoveAll<Pipeline.Interfaces.IProviderFactory>();
            services.AddSingleton(new Mock<Pipeline.Interfaces.IProviderFactory>().Object);

            services.RemoveAll<Pipeline.Interfaces.IQualityGateValidator>();
            services.AddSingleton(new Mock<Pipeline.Interfaces.IQualityGateValidator>().Object);

            // Replace stubs for dead Legacy dispatch dependencies
            services.RemoveAll<Pipeline.Interfaces.IConsolidationDispatchService>();
            services.AddSingleton<Pipeline.Interfaces.IConsolidationDispatchService>(
                new KestrelNoOpConsolidationDispatchService());
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

    private sealed class ApiKestrelDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public ApiKestrelDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new KestrelTestPipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class KestrelTestPipelineDbContext : PipelineDbContext
    {
        public KestrelTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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
                var indexesToRemove = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class NoOpKestrelDatabaseProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>Factory that omits Database__Host to test fast-fail behavior.</summary>
internal sealed class MissingDbHostFactory : WebApplicationFactory<Program>
{
    public MissingDbHostFactory()
    {
        // Must set env vars before host build — WebApplication.CreateBuilder reads them
        // during construction, before ConfigureWebHost callbacks run.
        Environment.SetEnvironmentVariable("Database__Host", null);
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", "any-key");
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Database__Host", null);
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }
        base.Dispose(disposing);
    }
}

/// <summary>Factory that omits AGENT_API_KEY to test fast-fail behavior.</summary>
internal sealed class MissingAgentApiKeyFactory : WebApplicationFactory<Program>
{
    public MissingAgentApiKeyFactory()
    {
        // Must set env vars before host build — WebApplication.CreateBuilder reads them
        // during construction, before ConfigureWebHost callbacks run.
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Database__Host", null);
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }
        base.Dispose(disposing);
    }
}

// Stub implementations for Legacy dispatch dependencies (dead after Spec 041)
file sealed class KestrelNoOpConsolidationDispatchService : CodingAgentWebUI.Pipeline.Interfaces.IConsolidationDispatchService
{
    public Task<CodingAgentWebUI.Pipeline.Interfaces.ConsolidationDispatchResult> TryDispatchAsync(CodingAgentWebUI.Pipeline.Models.ConsolidationRun r, CodingAgentWebUI.Pipeline.Models.ConsolidationRunType t,
        CodingAgentWebUI.Pipeline.Models.TemplateId? tid, string? f, string w, CancellationToken ct)
        => Task.FromResult(CodingAgentWebUI.Pipeline.Interfaces.ConsolidationDispatchResult.Failed);
    public Task<bool> TryDispatchToAgentAsync(CodingAgentWebUI.Pipeline.Models.RunId r, CodingAgentWebUI.Pipeline.Models.ConsolidationRunType t, CodingAgentWebUI.Pipeline.Models.TemplateId? tid,
        string w, CodingAgentWebUI.Pipeline.Models.AgentId a, CancellationToken ct)
        => Task.FromResult(false);
    public Task NotifyRunCancelledAsync(CodingAgentWebUI.Pipeline.Models.RunId r, CancellationToken ct) => Task.CompletedTask;
}
