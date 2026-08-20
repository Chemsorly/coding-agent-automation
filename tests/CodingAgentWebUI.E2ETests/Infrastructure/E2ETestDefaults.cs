using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shared configuration for the E2E web application factories.
/// </summary>
internal static class E2ETestDefaults
{
    /// <summary>
    /// Base URL handed to the monolith for <c>PipelineApi:BaseUrl</c> when the harness runs the
    /// Blazor app on its own.
    ///
    /// Spec 045 made the monolith fast-fail at startup when this is unset. Where no API host is
    /// started, every config, run-history and provider store is replaced by an in-memory fake and
    /// no request leaves the process. The address is deliberately unroutable so a code path that
    /// *does* try to reach the API fails rather than silently talking to something real.
    /// </summary>
    public const string UnreachableApiBaseUrl = "http://127.0.0.1:1";

    /// <summary>
    /// Applies the database settings both hosts need at startup.
    ///
    /// Spec 041 made PostgreSQL mandatory and Spec 045 left <c>AddPooledDbContextFactory</c> in
    /// the monolith for <c>KubernetesWorkDistributor</c> / <c>KubernetesJobCleanup</c>, so
    /// <c>AddWorkDistribution</c> throws "Database__Host is not configured" before the host is
    /// built. No connection is opened: <see cref="E2EInMemoryDatabase"/> swaps the provider and
    /// <c>Database__SkipStartupInit</c> suppresses the startup probe and migrations.
    /// </summary>
    public static void ApplyDatabaseEnvironment()
    {
        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
    }

    /// <summary>
    /// Clears the database settings on teardown so a factory cannot leak configuration into the
    /// next fixture in the same test process.
    /// </summary>
    public static void ClearDatabaseEnvironment()
    {
        foreach (var key in new[]
                 {
                     "Database__Host", "Database__Port", "Database__Username", "Database__Password",
                     "Database__Name", "Database__SslMode", "Database__MigrateOnStartup",
                     "Database__SkipStartupInit", "WorkDistribution__Mode", "AGENT_API_KEY",
                     "PipelineApi__BaseUrl", "PipelineApi__HubUrl", "PipelineLoop__ConfigCacheTtlSeconds"
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    /// Replaces the real Kubernetes client with a stub.
    ///
    /// <c>RegisterConsolidationServices</c> builds a <c>k8s.Kubernetes</c> from in-cluster config
    /// or <c>~/.kube/config</c> and throws "No usable Kubernetes configuration" when neither
    /// resolves. <c>LeaderElectionService</c> is a hosted service that takes it, so the monolith
    /// resolves it during startup and the whole harness dies wherever no kubeconfig exists —
    /// which is every CI container, and is why this only shows up outside a developer machine
    /// with Docker Desktop installed.
    /// </summary>
    public static void InstallKubernetesStub(IServiceCollection services)
    {
        services.RemoveAll<IKubernetes>();
        services.AddSingleton(new Mock<IKubernetes>().Object);
    }

    /// <summary>
    /// Gives each host build a fresh Serilog global logger.
    ///
    /// <c>UseSerilog</c> captures <c>Log.Logger</c> during host build and freezes it when it is a
    /// reloadable logger. The harness builds two hosts in one process, so leaving the first
    /// host's frozen logger in place makes the second build throw. Assigning a plain logger here
    /// hands each build something it can take ownership of.
    /// </summary>
    public static void ResetSerilogBootstrapLogger()
    {
        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateLogger();
    }
}
