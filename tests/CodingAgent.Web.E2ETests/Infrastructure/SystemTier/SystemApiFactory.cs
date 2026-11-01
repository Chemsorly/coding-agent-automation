using CodingAgent.Api;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Web.E2ETests.Infrastructure.SystemTier;

/// <summary>
/// Runs <c>CodingAgent.Api</c> on a real Kestrel port against real Postgres and Redis
/// containers. Unlike <see cref="ApiE2EWebApplicationFactory"/>, this factory:
/// <list type="bullet">
///   <item>Does NOT remove hosted services — <c>DatabaseReadinessMonitor</c>, <c>ChatJobDispatcher</c>, etc. run as real services.</item>
///   <item>Does NOT install an in-memory database — EF Core connects to the Postgres container.</item>
///   <item>Does NOT install an in-process lock or no-op probe — the real Postgres advisory lock and probe run.</item>
///   <item>Does NOT skip startup initialization — migrations run against the real Postgres container.</item>
/// </list>
/// Only the external integrations that don't exist in a test process are replaced:
/// the provider factory and the Kubernetes job client.
/// </summary>
public sealed class SystemApiFactory : WebApplicationFactory<ApiHostMarker>
{
    private readonly SystemContainers _containers;

    /// <summary>The fake provider factory injected into this host instance.</summary>
    public FakeProviderFactory Providers { get; }

    /// <summary>The fake Kubernetes job client injected into this host instance.</summary>
    public FakeKubernetesJobClient Kubernetes { get; }

    public SystemApiFactory(SystemContainers containers, FakeProviderFactory providers, FakeKubernetesJobClient kubernetes)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(kubernetes);
        _containers = containers;
        Providers = providers;
        Kubernetes = kubernetes;
        UseKestrel(0);
    }

    /// <summary>Base address of the running API, e.g. <c>http://localhost:12345</c>.</summary>
    public string ServerAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // ── Real database configuration ───────────────────────────────────────────
        // Set connection parameters directly from the container. Database__SkipStartupInit
        // is set to "false" so migrations run on startup — this is the point of the System tier.
        Environment.SetEnvironmentVariable("Database__Host", _containers.PostgresHost);
        Environment.SetEnvironmentVariable("Database__Port", _containers.PostgresPort.ToString());
        Environment.SetEnvironmentVariable("Database__Username", SystemContainers.PostgresUser);
        Environment.SetEnvironmentVariable("Database__Password", SystemContainers.PostgresPassword);
        Environment.SetEnvironmentVariable("Database__Name", SystemContainers.PostgresDatabase);
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "false");

        // ── Real Redis backplane ──────────────────────────────────────────────────
        Environment.SetEnvironmentVariable("SignalR__Redis__ConnectionString", _containers.RedisConnectionString);

        // ── Startup and auth ──────────────────────────────────────────────────────
        Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", "0");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", E2EWebApplicationFactory.TestApiKey);
        Environment.SetEnvironmentVariable("UserToken__SigningKey", E2ETestDefaults.UserTokenSigningKey);

        // Pod-dispatch settings (ChatJobDispatcher reads these at startup).
        E2ETestDefaults.ApplyDispatchEnvironment();

        // Each host build needs a fresh Serilog global logger (two hosts in one process).
        E2ETestDefaults.ResetSerilogBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Fast shutdown for tests.
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

            // ── Minimal replacements: only what cannot run in a test process ────
            // Hosted services, database, lock provider, probe, stores and history
            // all stay real — that is the point of this tier.
            services.RemoveAll<IProviderFactory>();
            services.AddSingleton<IProviderFactory>(Providers);

            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(Kubernetes);

            E2ETestDefaults.InstallKubernetesStub(services);
            E2ETestDefaults.InstallJobTemplates(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Clear all database and dispatch env vars so they don't bleed into the next host.
            E2ETestDefaults.ClearDatabaseEnvironment();
            Environment.SetEnvironmentVariable("SignalR__Redis__ConnectionString", null);
            Environment.SetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS", null);
        }
        base.Dispose(disposing);
    }
}
