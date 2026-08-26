using CodingAgentWebUI.Kubernetes;
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
    /// Applies the pod-dispatch settings <c>DispatchServiceOptionsFactory</c> reads.
    ///
    /// These are supplied as configuration rather than by hand-registering
    /// <c>ChatJobDispatcher</c> with a literal options object, so the harness exercises the same
    /// factory and the same <c>ValidateAndClamp</c> pass the real process does. The credential
    /// pool is the load-bearing one: chat dispatch claims a PVC from it and an empty pool means
    /// no pod is ever created.
    /// </summary>
    public static void ApplyDispatchEnvironment()
    {
        Environment.SetEnvironmentVariable("WorkDistribution__Namespace", "test");
        Environment.SetEnvironmentVariable("WorkDistribution__OrchestratorUrl", "http://test-orchestrator");
        Environment.SetEnvironmentVariable("WorkDistribution__AgentApiKeySecretName", "agent-api-key");
        Environment.SetEnvironmentVariable("WorkDistribution__AgentServiceAccountName", "agent-sa");
        Environment.SetEnvironmentVariable("WorkDistribution__CredentialPools__Kiro__0", "fake-pvc-0");
        Environment.SetEnvironmentVariable("WorkDistribution__CredentialPools__Kiro__1", "fake-pvc-1");
        Environment.SetEnvironmentVariable("WorkDistribution__Dispatch__ChatSessionMaxDurationSeconds", "7200");
        // 30s, matching the production default. The original 10s was meant to fail fast for broken
        // dispatches — but loaded CI runners can take 12s+ for a real Kestrel+WebSocket+SignalR
        // handshake on the second concurrent chat connection, which caused false failures.
        // The poll interval in ChatJobDispatcher was simultaneously reduced from 2s to 500ms, so a
        // working dispatch still finds the agent within one poll after it registers (~500ms) rather
        // than up to 2s. Net effect: broken dispatches fail within 30s instead of 10s (still well
        // within the 15-minute CI budget at 10 chat tests), and timing-sensitive connections never
        // time out.
        Environment.SetEnvironmentVariable("WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds", "30");
        Environment.SetEnvironmentVariable("WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds", "10");
    }

    /// <summary>
    /// Clears the settings applied above on teardown so a factory cannot leak configuration into
    /// the next fixture in the same test process.
    /// </summary>
    public static void ClearDatabaseEnvironment()
    {
        foreach (var key in new[]
                 {
                     "Database__Host", "Database__Port", "Database__Username", "Database__Password",
                     "Database__Name", "Database__SslMode", "Database__MigrateOnStartup",
                     "Database__SkipStartupInit", "AGENT_API_KEY",
                     "PipelineApi__BaseUrl", "PipelineApi__HubUrl", "PipelineLoop__ConfigCacheTtlSeconds",
                     "WorkDistribution__Namespace", "WorkDistribution__OrchestratorUrl",
                     "WorkDistribution__AgentApiKeySecretName", "WorkDistribution__AgentServiceAccountName",
                     "WorkDistribution__CredentialPools__Kiro__0", "WorkDistribution__CredentialPools__Kiro__1",
                     "WorkDistribution__Dispatch__ChatSessionMaxDurationSeconds",
                     "WorkDistribution__Dispatch__ChatPodConnectTimeoutSeconds",
                     "WorkDistribution__Dispatch__ChatTerminationGracePeriodSeconds"
                 })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    /// Installs the job templates both hosts dispatch against.
    ///
    /// <para>
    /// <c>JobTemplateStore</c> loads <c>/app/config/job-templates.yaml</c>, which exists only
    /// in-cluster where the ConfigMap is mounted, so every host in the harness has to be given a
    /// literal set instead. Both hosts are given the <em>same</em> set from here rather than each
    /// declaring its own: the API dispatches chat pods and the monolith owns work-item cleanup, and
    /// when the two lists drifted apart a chat test asking for a selector only one side knew about
    /// failed with "no template for selector" — a harness artifact that looks exactly like a
    /// product bug.
    /// </para>
    ///
    /// <para>
    /// <c>maxConcurrent</c> is high enough to stay out of the way; a test asserting on concurrency
    /// limits sets its own.
    /// </para>
    /// </summary>
    public static void InstallJobTemplates(IServiceCollection services)
    {
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(JobTemplateStore.LoadFromYaml("""
            - labels: "kiro,dotnet"
              image: "chemsorly/coding-agent:kiro-dotnet10-latest"
              imagePullPolicy: "Always"
              providerType: "kiro"
              maxConcurrent: 5
            - labels: "kiro,python"
              image: "chemsorly/coding-agent:kiro-python-latest"
              imagePullPolicy: "Always"
              providerType: "kiro"
              maxConcurrent: 5
            - labels: "kiro,node"
              image: "chemsorly/coding-agent:kiro-node-latest"
              imagePullPolicy: "Always"
              providerType: "kiro"
              maxConcurrent: 5
            - labels: "opencode,dotnet"
              image: "chemsorly/coding-agent:opencode-dotnet10-latest"
              imagePullPolicy: "Always"
              providerType: "opencode"
              maxConcurrent: 5
            """));
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
