using CodingAgent.JobController.Reconciliation;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Web.E2ETests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Hosts the real <c>CodingAgent.JobController</c> process in-process against the E2E API host,
/// replacing only the Kubernetes client and leader-election service with test doubles.
///
/// <para>
/// This factory is the missing link in E2E coverage: the existing harness dispatches through
/// <see cref="FakeJobController"/>, which bypasses <see cref="ReconciliationLoop"/> entirely.
/// Hosting the real JobController lets the reconciliation loop — including
/// <c>CleanupOrphansAsync</c> and <c>ClassifyJobFailureReasonAsync</c> — run against the real
/// API HTTP stack and the real in-memory database.
/// </para>
///
/// <para>
/// Three mandatory overrides in <c>ConfigureWebHost</c>:
/// <list type="number">
///   <item><c>RemoveAll&lt;IHostedService&gt;()</c> — prevents the reconciliation loop and all
///     other background services from auto-starting; tests drive them directly.</item>
///   <item><c>RemoveAll&lt;ILeaderElectionService&gt;()</c> + <see cref="AlwaysLeaderElectionService"/>
///     — <c>LeaderElectionService</c> is <c>sealed</c> and requires a live Kubernetes lease.
///     The stub permanently reports <c>IsLeader = true</c> so <c>ReconciliationService</c>
///     would be immediately runnable if started, and <c>ReconciliationLoop</c> can always be
///     driven directly.</item>
///   <item><c>RemoveAll&lt;IKubernetesJobClient&gt;()</c> + shared <see cref="FakeKubernetesJobClient"/>
///     — the same fake instance the API host uses, so both sides see the same job state.</item>
/// </list>
/// </para>
///
/// <para>
/// The factory uses <see cref="CodingAgent.JobController.JobControllerHostMarker"/> rather than
/// the global <c>Program</c> class to avoid ambiguity: the E2E harness hosts three assemblies
/// that each declare a top-level <c>Program</c> (<c>CodingAgent.Web</c>, <c>CodingAgent.Api</c>,
/// and <c>CodingAgent.JobController</c>). The marker is in a distinct namespace, so the compiler
/// can resolve it unambiguously.
/// </para>
/// </summary>
public sealed class JobControllerE2EWebApplicationFactory
    : WebApplicationFactory<CodingAgent.JobController.JobControllerHostMarker>
{
    private readonly string _apiBaseUrl;
    private readonly FakeKubernetesJobClient _fakeK8sClient;
    private readonly string _apiKey;

    public JobControllerE2EWebApplicationFactory(
        string apiBaseUrl,
        FakeKubernetesJobClient fakeK8sClient,
        string apiKey)
    {
        _apiBaseUrl = apiBaseUrl;
        _fakeK8sClient = fakeK8sClient;
        _apiKey = apiKey;

        // Set required environment variables immediately on construction.
        // Program.cs reads these via builder.Configuration (env vars) and via
        // Environment.GetEnvironmentVariable (in-cluster guard) before ConfigureWebHost
        // is called. Setting them here ensures they are available throughout the factory lifecycle.
        //
        // PipelineApi__BaseUrl → PipelineApi:BaseUrl (double-underscore → colon in .NET config)
        //
        // TODO [WARNING]: These Environment.SetEnvironmentVariable calls write to the process-wide
        // environment, which is not isolated to this factory's scope. Parallel tests or other
        // factories in the same process will see these mutated values. Additionally, there is no
        // Dispose override to restore the previous values, leaving the process env permanently
        // mutated. Fix: (a) add a Dispose override that restores the original env var values
        // captured in the constructor, and (b) investigate whether Program.cs can be changed to
        // read PipelineApi:BaseUrl and AGENT_API_KEY via IWebHostBuilder.UseSetting /
        // ConfigureAppConfiguration so the process-level SetEnvironmentVariable is not needed.
        Environment.SetEnvironmentVariable("PipelineApi__BaseUrl", _apiBaseUrl);
        Environment.SetEnvironmentVariable("AGENT_API_KEY", _apiKey);
        // The in-cluster guard in Program.cs checks this directly via Environment.GetEnvironmentVariable,
        // not via builder.Configuration, so UseEnvironment("Test") alone is not sufficient.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
    }

    /// <summary>
    /// Exposes the <see cref="ReconciliationLoop"/> singleton for direct deterministic invocation
    /// in tests. Tests call <c>ReconcileOnceAsync</c>, <c>CleanupOrphansAsync</c>, etc. directly
    /// rather than waiting for the polling loop.
    /// </summary>
    public ReconciliationLoop ReconciliationLoop =>
        Services.GetRequiredService<ReconciliationLoop>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseEnvironment sets it in the configuration system (in addition to the env var set
        // in the constructor). Belt-and-suspenders — both are needed.
        builder.UseEnvironment("Test");

        // Third host in the process — give Serilog a fresh bootstrap logger
        E2ETestDefaults.ResetSerilogBootstrapLogger();

        builder.ConfigureServices(services =>
        {
            // ── 1. Remove all background services ────────────────────────────────────
            // Prevents ReconciliationService, LeaderElectionService, and everything else
            // from auto-starting. Tests drive ReconciliationLoop directly.
            services.RemoveAll<IHostedService>();

            // ── 2. Replace ILeaderElectionService with always-leader stub ────────────
            // LeaderElectionService is sealed and requires a live Kubernetes lease.
            // Without this replacement, ReconciliationService.RunLeadershipTermAsync polls
            // IsLeader every 2 s indefinitely (it was never started, so _isLeader stays false).
            services.RemoveAll<ILeaderElectionService>();
            services.AddSingleton<ILeaderElectionService, AlwaysLeaderElectionService>();

            // ── 3. Replace IKubernetesJobClient with the shared fake ────────────────
            // Same instance the API host uses so both see identical job state.
            services.RemoveAll<IKubernetesJobClient>();
            services.AddSingleton<IKubernetesJobClient>(_fakeK8sClient);

            // ── 4. Stub out IKubernetes (used by AddKubernetesClient) ─────────────
            // The real IKubernetes client tries to load in-cluster or kubeconfig.
            // InstallKubernetesStub replaces it with a Mock<IKubernetes>.
            E2ETestDefaults.InstallKubernetesStub(services);

            // ── 5. Reduce shutdown timeout for faster teardown ────────────────────
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        });
    }
}
