using CodingAgent.Agent;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Runs the real <see cref="WorkItemAgentService"/> in-process against the E2E API host, exactly
/// as a work-item pod does in production, but with fake providers substituted so no LLM,
/// git remote, or cluster is needed.
///
/// <para>
/// The harness builds a minimal <see cref="WebApplication"/> that mirrors what
/// <c>CodingAgent.Agent/Program.cs</c> does for work-item mode, overriding:
/// <list type="bullet">
///   <item><see cref="IProviderFactory"/> with the test's <see cref="FakeProviderFactory"/> so
///         the scripted agent provider and in-memory repository provider are used.</item>
///   <item><see cref="IQualityGateValidator"/> with <see cref="ConfigurableQualityGateValidator"/>
///         so quality gates always pass without running real builds.</item>
///   <item><see cref="IBrainUpdateService"/> with a no-op so brain writes do nothing.</item>
/// </list>
/// </para>
///
/// <para>
/// The real MessagePack protocol (<see cref="AgentHubProtocolExtensions.AddAgentHubProtocol"/>)
/// is used for the SignalR connection so wire-format regressions are caught end-to-end.
/// </para>
/// </summary>
public sealed class RealAgentWorkerHarness : IAsyncDisposable
{
    private WebApplication? _app;
    private Task? _runTask;

    /// <summary>
    /// Starts the agent in-process against <paramref name="agentHubUrl"/> for the given
    /// <paramref name="workItemId"/>.
    ///
    /// The agent will connect, fetch its assignment, execute the pipeline using the provided
    /// fake providers, and report completion. Completion (success or failure) can be observed
    /// by polling the WorkItem status via the fixture's work-item client.
    /// </summary>
    public async Task StartAsync(
        string agentHubUrl,
        string apiKey,
        string agentId,
        string workItemId,
        FakeProviderFactory fakeProviders,
        ConfigurableQualityGateValidator qualityGateValidator,
        CancellationToken ct = default)
    {
        // The agent reads AGENT_API_KEY_FILE (pre-derived key path) or AGENT_API_KEY directly.
        // For the harness we set AGENT_API_KEY to the pre-derived key (derived from master key +
        // agentId, exactly as the real dispatch does), so KeyIsPreDerived = true.
        var derivedKey = HubConnectionManager.DeriveKey(apiKey, agentId);

        var config = new AgentStartupConfig
        {
            AgentApiKey = derivedKey,
            OrchestratorUrl = agentHubUrl,
            AgentId = new AgentId(agentId),
            WorkItemId = workItemId,
            IsWorkItemMode = true,
            KeyIsPreDerived = true
        };

        var builder = WebApplication.CreateBuilder();

        // Suppress noisy output from the agent host during tests
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // random port, not used

        // ── KiroCliLib (needed by LocalPipelineExecutor even though we replace the provider) ──
        // TODO [WARNING]: WorkspaceDirectory uses a shared temp path that is never cleaned up between
        // test runs or after DisposeAsync. Concurrent or re-run tests accumulate workspace dirs in
        // the temp folder. Consider using Path.Combine(Path.GetTempPath(), $"e2e-agent-workspaces-{Guid.NewGuid():N}")
        // and deleting it in DisposeAsync to avoid stale .agent/*.md files across retries.
        var kiroConfig = new Configuration
        {
            KiroCliPath = AgentDefaults.KiroCliPath,
            UseWsl = false,
            WorkspaceDirectory = Path.Combine(Path.GetTempPath(), "e2e-agent-workspaces")
        };
        builder.Services.AddSingleton(kiroConfig);
        builder.Services.AddSingleton<IKiroCliOrchestrator>(sp =>
        {
            var cfg = sp.GetRequiredService<Configuration>();
            return new KiroCliOrchestrator(cfg, Serilog.Log.Logger);
        });

        // ── Pipeline defaults ──
        var defaultPipelineConfig = new PipelineConfiguration
        {
            // TODO [WARNING]: Same shared temp path concern as above — should be per-run and cleaned up.
            WorkspaceBaseDirectory = Path.Combine(Path.GetTempPath(), "e2e-agent-workspaces")
        };
        builder.Services.AddSingleton(defaultPipelineConfig);
        builder.Services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

        // ── Pipeline services ──
        builder.Services.AddPipelineServices(Serilog.Log.Logger);
        // Replace the default brain update service with a no-op (no git remote needed)
        builder.Services.RemoveAll<IBrainUpdateService>();
        builder.Services.AddSingleton<IBrainUpdateService, NoOpBrainUpdateService>();

        // ── Agent runtime options ──
        builder.Services.AddOptions<AgentRuntimeOptions>();
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<AgentRuntimeOptions>,
            AgentRuntimeOptionsSetup>();
        builder.Services.AddSingleton<AgentRuntimeOptions>(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentRuntimeOptions>>().Value);

        // ── OpenCode HTTP client (registered even when unused — see Program.cs) ──
        builder.Services.AddHttpClient(AgentDefaults.OpenCodeHttpClientName);

        // ── Agent identity ──
        builder.Services.Add(ServiceDescriptor.Singleton(typeof(AgentId), config.AgentId));

        // ── Hub connection manager ──
        builder.Services.AddSingleton<IHubConnectionManagerFactory>(sp =>
            new HubConnectionManagerFactory(
                config.OrchestratorUrl,
                config.AgentId,
                config.AgentApiKey,
                Serilog.Log.Logger,
                keyIsPreDerived: config.KeyIsPreDerived));
        builder.Services.AddSingleton<IHubConnectionManager>(sp =>
            sp.GetRequiredService<IHubConnectionManagerFactory>().Create());

        // ── Fake providers (injected as ProviderFactoryOverride into LocalPipelineExecutorDependencies) ──
        // This is the key substitution: instead of the real AgentProviderFactory creating KiroCli
        // or GitHub providers from ProviderConfig, the FakeProviderFactory returns
        // InMemoryRepositoryProvider and ScriptedAgentProvider regardless of config.
        // No DI registration needed — the override is passed directly to LocalPipelineExecutor.

        // ── Quality gate validator fake ──
        // Must be registered AFTER AddPipelineServices which also registers IQualityGateValidator.
        // The last registration wins for GetRequiredService<T>() when not using RemoveAll+re-add,
        // but AddPipelineServices uses AddSingleton (not TryAddSingleton), so we must remove first.
        builder.Services.RemoveAll<IQualityGateValidator>();
        builder.Services.AddSingleton<IQualityGateValidator>(qualityGateValidator);

        // ── Pipeline executor (uses our fake IProviderFactory via ProviderFactoryOverride) ──
        builder.Services.AddSingleton<IPipelineReporterFactory>(sp =>
            new PipelineReporterFactory(Serilog.Log.Logger));
        builder.Services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
            new LocalPipelineExecutorDependencies(
                Orchestrator: sp.GetRequiredService<IKiroCliOrchestrator>(),
                HttpClientFactory: sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                DefaultPipelineConfig: sp.GetRequiredService<PipelineConfiguration>(),
                QualityGateValidator: sp.GetRequiredService<IQualityGateValidator>(),
                Logger: Serilog.Log.Logger,
                BrainUpdateService: sp.GetRequiredService<IBrainUpdateService>(),
                ReporterFactory: sp.GetRequiredService<IPipelineReporterFactory>(),
                ProviderFactoryOverride: fakeProviders)));

        // ── Consolidation executor (no-op for work-item mode) ──
        // TODO [WARNING]: LocalConsolidationExecutor is constructed with `new` directly rather than
        // going through the service provider. If the constructor signature changes or the type becomes
        // IDisposable/IAsyncDisposable in the future, this will silently hold a stale construction.
        // Consider registering it via the service collection for consistency with IPipelineExecutor above.
        builder.Services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            Serilog.Log.Logger));

        // ── Work-item mode services ──
        builder.Services.AddK8sModeServices(config, Serilog.Log.Logger);

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

        _app = builder.Build();
        _app.MapHealthEndpoints();

        // Start the host — WorkItemAgentService runs as a BackgroundService.
        // It stops the application when the work item completes.
        // TODO [WARNING]: If _app.StartAsync throws after _app is assigned, DisposeAsync will never be
        // called by the caller (the harness has not yet been returned as `await using`), leaking the
        // WebApplication and its registered services. Wrap these two lines so that on StartAsync failure
        // _app is disposed and rethrown, or defer field assignment until after successful start.
        await _app.StartAsync(ct);
        // TODO [WARNING]: _runTask captures `ct` (the caller's CancellationToken). WaitForCompletionAsync
        // links a timeout-CTS to the same `ct`, so when the timeout fires it cancels `ct`, faulting
        // _runTask. The `when (!ct.IsCancellationRequested)` guard may not correctly distinguish a
        // caller-cancel from a harness timeout if both fire simultaneously. Consider using an internal
        // CancellationTokenSource owned by the harness for WaitForShutdownAsync, cancelled from DisposeAsync,
        // to make token ownership unambiguous.
        _runTask = _app.WaitForShutdownAsync(ct);
    }

    /// <summary>
    /// Waits for the agent to complete (host stops itself after the work item finishes).
    /// </summary>
    public async Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_runTask is null)
            throw new InvalidOperationException("Agent not started. Call StartAsync first.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await _runTask.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Real agent worker did not complete within {timeout.TotalSeconds}s");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            // TODO [WARNING]: StopAsync exceptions are swallowed (best-effort). If StopAsync hangs or
            // _runTask faulted without the host stopping cleanly, in-process agent threads or SignalR
            // connections may remain alive and pollute the shared E2EFixture hub for subsequent tests.
            // Consider adding a short cancellable timeout to StopAsync and logging swallowed exceptions.
            try { await _app.StopAsync(CancellationToken.None); }
            catch { /* best-effort */ }
            await _app.DisposeAsync();
        }
    }

    /// <summary>No-op brain update service for tests — avoids git remote calls.</summary>
    private sealed class NoOpBrainUpdateService : IBrainUpdateService
    {
        public Task<IReadOnlyList<string>> DetectChangesAsync(string brainPath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public BrainValidationResult Validate(string brainPath, RunId runId, IReadOnlyList<string> changedFiles)
            => new() { SessionLogCreated = true, OperationLogUpdated = true, EntryFormatValid = true };

        public Task AppendFallbackLogEntryAsync(string brainPath, RunId runId, IReadOnlyList<string> modifiedFiles, CancellationToken ct)
            => Task.CompletedTask;

        public Task<BrainSyncResult> CommitAndPushAsync(string brainPath, RunId runId, string issueIdentifier,
            IRepositoryProvider brainProvider, CancellationToken ct, int maxPushRetries = 3)
            => Task.FromResult(new BrainSyncResult());
    }
}
