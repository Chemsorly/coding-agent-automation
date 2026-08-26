using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
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
/// xUnit collection for AgentHub gate tests. Ensures a single
/// <see cref="AgentHubGateKestrelFactory"/> instance (one Kestrel host) is shared
/// across all tests in <see cref="AgentHubGateTests"/>. DisableParallelization
/// prevents env-var collisions with the shared <see cref="ApiWebApplicationFactory"/>.
/// </summary>
[CollectionDefinition("AgentHubGateCollection", DisableParallelization = true)]
public sealed class AgentHubGateCollection : ICollectionFixture<AgentHubGateKestrelFactory> { }

/// <summary>
/// Hub integration gate tests — required to pass before Tasks 8 and 9 (delete monolith
/// MapHub and WorkItemEndpoints). See Spec 044 Req 6.1 and Task 7.
///
/// All tests use real SignalR clients connected to a real Kestrel instance (not mocked).
/// Event delivery is asserted via <see cref="TaskCompletionSource{T}"/> with a timeout.
///
/// Note: Tests share one <see cref="AgentHubGateKestrelFactory"/> instance via
/// <see cref="IClassFixture{T}"/> so the Kestrel host starts only once per test class.
/// Each test uses unique agent IDs and run IDs to avoid state conflicts.
/// </summary>
[Collection("AgentHubGateCollection")]
public sealed class AgentHubGateTests
{
    private readonly AgentHubGateKestrelFactory _factory;

    public AgentHubGateTests(AgentHubGateKestrelFactory factory)
    {
        _factory = factory;
    }

    // ── Req 6.1: Full agent lifecycle gate ─────────────────────────────────────

    /// <summary>
    /// Req 6.1, steps 1-6: Full agent lifecycle through the API hub.
    /// 1. Mock agent connects and calls RegisterAgent.
    /// 2. Test UI client calls SubscribeToRun.
    /// 3. Mock agent calls ReportOutputLines.
    /// 4. Assert UI client receives OnOutputLines (real SignalR delivery, not mocked).
    /// 5. Mock agent calls ReportJobCompleted.
    /// 6. Assert run history service was called (via post-run DB query).
    /// </summary>
    [Fact]
    public async Task FullAgentLifecycle_RegisterOutputLinesCompleted_UIClientReceivesEvents()
    {
        using var client = _factory.CreateClient(); // triggers host start
        var serverAddress = _factory.ServerAddress;

        var agentId = $"gate-agent-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        // Step 1: Connect mock agent and register
        var agentConnection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await agentConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            agentConnection.State.Should().Be(HubConnectionState.Connected);

            await agentConnection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "gate-test-host",
                Labels = ["dotnet"]
            });

            // Seed: add PipelineRun and mark agent as Busy with this job
            // (required for [RequiresActiveJob] filter to pass)
            SeedRunAndBusyAgent(_factory, agentId, jobId);

            // Step 2: Connect UI client and subscribe to the run
            var uiConnection = BuildUiConnection(serverAddress);
            var outputLinesReceived = new TaskCompletionSource<(string JobId, IReadOnlyList<string> Lines)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            uiConnection.On<string, IReadOnlyList<string>>(
                HubMethodNames.OnOutputLines,
                (jId, lines) => outputLinesReceived.TrySetResult((jId, lines)));

            try
            {
                await uiConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
                uiConnection.State.Should().Be(HubConnectionState.Connected);

                await uiConnection.InvokeAsync("SubscribeToRun", jobId);

                // Step 3: Mock agent calls ReportOutputLines
                var testLines = new List<string> { "output line 1", "output line 2" };
                await agentConnection.InvokeAsync("ReportOutputLines", new JobId(jobId), testLines);

                // Step 4: Assert UI client receives OnOutputLines within 10 seconds
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                cts.Token.Register(() => outputLinesReceived.TrySetCanceled());

                var result = await outputLinesReceived.Task;
                result.JobId.Should().Be(jobId);
                result.Lines.Should().Equal(testLines);

                // Step 5: Mock agent calls ReportJobCompleted
                var payload = new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                await agentConnection.InvokeAsync("ReportJobCompleted", new JobId(jobId), payload);

                // Step 6: Assert run was persisted to history
                // Give async DB write a moment to complete
                await Task.Delay(500);
                var historyService = _factory.Services.GetRequiredService<IPipelineRunHistoryService>();
                var history = await historyService.GetRunHistoryAsync();
                history.Should().Contain(r => r.RunId == jobId,
                    $"run {jobId} must be in history after ReportJobCompleted");
            }
            finally
            {
                await uiConnection.StopAsync();
                await uiConnection.DisposeAsync();
            }
        }
        finally
        {
            await agentConnection.StopAsync();
            await agentConnection.DisposeAsync();
        }
    }

    // ── Req 6.2: ReportStepTransition updates in-memory PipelineRun ────────────

    /// <summary>
    /// Req 6.2: ReportStepTransition updates in-memory PipelineRun.CurrentStep.
    /// </summary>
    [Fact]
    public async Task ReportStepTransition_UpdatesInMemoryPipelineRunCurrentStep()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var agentId = $"step-agent-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        var agentConnection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await agentConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            await agentConnection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "step-test-host",
                Labels = ["dotnet"]
            });

            SeedRunAndBusyAgent(_factory, agentId, jobId);

            // Invoke ReportStepTransition
            await agentConnection.InvokeAsync("ReportStepTransition",
                new JobId(jobId), PipelineStep.GeneratingCode, DateTimeOffset.UtcNow, (Dictionary<string, string>?)null);

            // Give synchronous in-memory update a moment (it's synchronous but hub dispatch is async)
            await Task.Delay(200);

            // Assert in-memory PipelineRun.CurrentStep updated
            var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
            var run = runService.GetRun(new RunId(jobId));
            run.Should().NotBeNull();
            run!.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        }
        finally
        {
            await agentConnection.StopAsync();
            await agentConnection.DisposeAsync();
        }
    }

    // ── Req 6.2: SubscribeToRun second client receives events end-to-end ───────

    /// <summary>
    /// Req 6.2: SubscribeToRun → second UI client receives subsequent events end-to-end.
    /// Two UI clients subscribe. Agent sends ReportOutputLines. Both clients receive it.
    /// </summary>
    [Fact]
    public async Task SubscribeToRun_TwoClients_BothReceiveSubsequentEvents()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var agentId = $"multi-agent-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        var agentConnection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await agentConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            await agentConnection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "multi-test-host",
                Labels = ["dotnet"]
            });

            SeedRunAndBusyAgent(_factory, agentId, jobId);

            // Connect two separate UI clients and subscribe both
            var ui1 = BuildUiConnection(serverAddress);
            var ui2 = BuildUiConnection(serverAddress);

            var received1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            ui1.On<string, IReadOnlyList<string>>(
                HubMethodNames.OnOutputLines,
                (_, lines) => received1.TrySetResult(string.Join(",", lines)));
            ui2.On<string, IReadOnlyList<string>>(
                HubMethodNames.OnOutputLines,
                (_, lines) => received2.TrySetResult(string.Join(",", lines)));

            try
            {
                await ui1.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
                await ui2.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

                await ui1.InvokeAsync("SubscribeToRun", jobId);
                await ui2.InvokeAsync("SubscribeToRun", jobId);

                // Agent sends output lines
                await agentConnection.InvokeAsync("ReportOutputLines",
                    new JobId(jobId), new List<string> { "line-a", "line-b" });

                // Both clients must receive within timeout
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                cts.Token.Register(() =>
                {
                    received1.TrySetCanceled();
                    received2.TrySetCanceled();
                });

                var r1 = await received1.Task;
                var r2 = await received2.Task;
                r1.Should().Be("line-a,line-b");
                r2.Should().Be("line-a,line-b");
            }
            finally
            {
                await ui1.StopAsync();
                await ui2.StopAsync();
                await ui1.DisposeAsync();
                await ui2.DisposeAsync();
            }
        }
        finally
        {
            await agentConnection.StopAsync();
            await agentConnection.DisposeAsync();
        }
    }

    // ── Req 5.3a: Agent cannot subscribe to another agent's run ────────────────

    /// <summary>
    /// Req 5.3a: SubscribeToRun rejects an agent connection that is not assigned to the run.
    /// An agent authenticated as agentA should be rejected when subscribing to agentB's run.
    /// </summary>
    [Fact]
    public async Task SubscribeToRun_AgentConnectionNotAssignedToRun_IsRejected()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var agentA = $"agent-a-{Guid.NewGuid():N}";
        var agentB = $"agent-b-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        // Register agentA
        var connA = BuildAgentConnection(serverAddress, agentA);
        // Register agentB
        var connB = BuildAgentConnection(serverAddress, agentB);

        try
        {
            await connA.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            await connB.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            await connA.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentA),
                Hostname = "host-a",
                Labels = []
            });
            await connB.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentB),
                Hostname = "host-b",
                Labels = []
            });

            // Seed: run is assigned to agentA, not agentB
            SeedRunAndBusyAgent(_factory, agentA, jobId);

            // agentB (a different agent connection) tries to subscribe to agentA's run
            Func<Task> act = () => connB.InvokeAsync("SubscribeToRun", jobId);
            await act.Should().ThrowAsync<Exception>(
                "an agent connection not assigned to the run must be rejected by SubscribeToRun (Req 5.3a)");
        }
        finally
        {
            await connA.StopAsync();
            await connB.StopAsync();
            await connA.DisposeAsync();
            await connB.DisposeAsync();
        }
    }

    // ── Req C2.1a: Chat pod cutover — chat agent registers on API hub ────────────

    /// <summary>
    /// Req C2.1a: A chat pod launched after cutover must successfully register on the API hub.
    ///
    /// Before this spec, ORCHESTRATOR_URL pointed to the monolith. After cutover it points to
    /// the API. This test proves the API hub accepts a chat-style agent connection —
    /// the same path a real chat pod takes when it starts and calls RegisterAgent.
    ///
    /// A chat pod is distinguished from a work-item pod by:
    /// - No ActiveJob (no --work-item-id)
    /// - Labels include "chat=true" and "chat-session-id=..." (see AgentConnectionLifecycle.cs)
    ///
    /// Without this test the failure is invisible: the pod starts, reports healthy,
    /// and simply never appears as a connected agent.
    /// </summary>
    [Fact]
    public async Task ChatPodCutover_AgentRegistersOnApiHub_NotMonolith()
    {
        using var client = _factory.CreateClient(); // triggers host start
        var serverAddress = _factory.ServerAddress;

        // Chat agent ID — no work-item ID, chat-mode labels
        var chatSessionId = Guid.NewGuid().ToString("N");
        var agentId = $"caa-chat-{Guid.NewGuid():N}";

        var agentConnection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await agentConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            agentConnection.State.Should().Be(HubConnectionState.Connected,
                "chat pod must be able to connect to the API hub after ORCHESTRATOR_URL cutover");

            // Chat pod registration: no ActiveJob, chat-mode labels
            // (mirrors AgentConnectionLifecycle.BuildRegistrationMessage when _isChatMode is true)
            var chatRegistration = new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "chat-pod-test-host",
                Labels = ["dotnet", "chat=true", $"chat-session-id={chatSessionId}"],
                ActiveJob = null  // no --work-item-id: this is a chat pod
            };

            Func<Task> act = () => agentConnection.InvokeAsync("RegisterAgent", chatRegistration);
            await act.Should().NotThrowAsync(
                "RegisterAgent must succeed for a chat-style agent on the API hub (Req C2.1a)");

            // Confirm the registry accepted it
            var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
            var entry = registry.GetByAgentId(agentId);
            entry.Should().NotBeNull(
                "API hub must track the chat pod in the agent registry after RegisterAgent");
            entry!.Labels.Should().Contain("chat=true",
                "chat pod labels must be preserved in the registry");
        }
        finally
        {
            await agentConnection.StopAsync();
            await agentConnection.DisposeAsync();
        }
    }

    // ── Req 3.4a: SubscribeToRun pushes OutputRingBuffer backlog ────────────────

    /// <summary>
    /// Req 3.4a: SubscribeToRun must immediately push buffered output lines to the new
    /// subscriber so a UI client navigating to a mid-run page sees existing output
    /// without waiting for a subsequent ReportOutputLines call.
    ///
    /// Steps:
    /// 1. Agent connects, registers, and calls ReportOutputLines to buffer lines.
    /// 2. UI client connects and calls SubscribeToRun.
    /// 3. Assert the UI client receives OnOutputLines with the buffered lines immediately
    ///    — no further ReportOutputLines is sent after SubscribeToRun.
    /// </summary>
    [Fact]
    public async Task SubscribeToRun_AfterAgentReportedOutput_UIClientReceivesBacklogImmediately()
    {
        using var client = _factory.CreateClient(); // triggers host start
        var serverAddress = _factory.ServerAddress;

        var agentId = $"backlog-agent-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        // Step 1: Agent connects, registers, seeds run, and reports output lines
        var agentConnection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await agentConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            agentConnection.State.Should().Be(HubConnectionState.Connected);

            await agentConnection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "backlog-test-host",
                Labels = ["dotnet"]
            });

            SeedRunAndBusyAgent(_factory, agentId, jobId);

            // Report output lines BEFORE the UI client subscribes — these go into the ring buffer.
            var bufferedLines = new List<string> { "buffered-line-1", "buffered-line-2", "buffered-line-3" };
            await agentConnection.InvokeAsync("ReportOutputLines", new JobId(jobId), bufferedLines);

            // Step 2: UI client connects and subscribes — no more ReportOutputLines after this point.
            var uiConnection = BuildUiConnection(serverAddress);
            var backlogReceived = new TaskCompletionSource<(string JobId, IReadOnlyList<string> Lines)>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            uiConnection.On<string, IReadOnlyList<string>>(
                HubMethodNames.OnOutputLines,
                (jId, lines) => backlogReceived.TrySetResult((jId, lines)));

            try
            {
                await uiConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
                uiConnection.State.Should().Be(HubConnectionState.Connected);

                // Subscribe — backlog push must happen as part of SubscribeToRun (Req 3.4a).
                await uiConnection.InvokeAsync("SubscribeToRun", jobId);

                // Step 3: Assert UI client receives the buffered lines within 10 seconds.
                // No further ReportOutputLines is sent — the push must come from the ring buffer.
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                cts.Token.Register(() => backlogReceived.TrySetCanceled());

                var result = await backlogReceived.Task;
                result.JobId.Should().Be(jobId);
                result.Lines.Should().Equal(bufferedLines,
                    "SubscribeToRun must push the OutputRingBuffer backlog to the new subscriber (Req 3.4a)");
            }
            finally
            {
                await uiConnection.StopAsync();
                await uiConnection.DisposeAsync();
            }
        }
        finally
        {
            await agentConnection.StopAsync();
            await agentConnection.DisposeAsync();
        }
    }

    // ── AgentAuthorizationFilter installation ───────────────────────────────────
    //
    // These three tests fail if AgentAuthorizationFilter is registered in DI as IHubFilter
    // but never installed via HubOptions.AddFilter<T>(). Registering an IHubFilter in the
    // service collection does NOT activate it — the dispatcher reads HubOptions.HubFilters,
    // which only AddFilter populates. Without them the filter can silently go dark and every
    // [RequiresActiveJob] check on the hub stops being enforced.

    /// <summary>
    /// An agent-authenticated connection that never called RegisterAgent must not be able to
    /// invoke agent methods. Proves the filter's registration gate is active.
    /// </summary>
    [Fact]
    public async Task UnregisteredAgentConnection_InvokingAgentMethod_IsRejected()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var agentId = $"unregistered-{Guid.NewGuid():N}";
        var jobId = Guid.NewGuid().ToString("N");

        var connection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            // Deliberately skip RegisterAgent.
            Func<Task> act = () => connection.InvokeAsync(
                "ReportOutputLines", new JobId(jobId), new List<string> { "line" });

            (await act.Should().ThrowAsync<HubException>(
                    "AgentAuthorizationFilter must reject hub calls from connections that have not registered"))
                .WithMessage("*not registered*");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// An operator/UI connection may subscribe to run groups but must not be able to drive the
    /// agent-facing surface (reporting output, completing jobs).
    /// </summary>
    [Fact]
    public async Task OperatorConnection_InvokingAgentMethod_IsRejected()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var jobId = Guid.NewGuid().ToString("N");

        var uiConnection = BuildUiConnection(serverAddress);
        try
        {
            await uiConnection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            // SubscribeToRun is allowed for operators — this is the UI's live-streaming path.
            await uiConnection.InvokeAsync("SubscribeToRun", jobId);

            // ReportOutputLines is not.
            Func<Task> act = () => uiConnection.InvokeAsync(
                "ReportOutputLines", new JobId(jobId), new List<string> { "line" });

            (await act.Should().ThrowAsync<HubException>(
                    "operator connections must not be able to report agent output"))
                .WithMessage("*not available to operator connections*");
        }
        finally
        {
            await uiConnection.StopAsync();
            await uiConnection.DisposeAsync();
        }
    }

    /// <summary>
    /// A registered agent must not be able to report against a job it does not own.
    /// Proves the [RequiresActiveJob] branch of the filter is reached.
    /// </summary>
    [Fact]
    public async Task RegisteredAgent_ReportingForForeignJob_IsRejected()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var agentId = $"owner-{Guid.NewGuid():N}";
        var ownJobId = Guid.NewGuid().ToString("N");
        var foreignJobId = Guid.NewGuid().ToString("N");

        var connection = BuildAgentConnection(serverAddress, agentId);
        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            await connection.InvokeAsync("RegisterAgent", new AgentRegistrationMessage
            {
                AgentId = new AgentId(agentId),
                Hostname = "owner-host",
                Labels = []
            });

            SeedRunAndBusyAgent(_factory, agentId, ownJobId);

            // Reporting for its own job is fine.
            await connection.InvokeAsync("ReportOutputLines", new JobId(ownJobId), new List<string> { "mine" });

            // Reporting for someone else's job is not.
            Func<Task> act = () => connection.InvokeAsync(
                "ReportOutputLines", new JobId(foreignJobId), new List<string> { "theirs" });

            (await act.Should().ThrowAsync<HubException>(
                    "[RequiresActiveJob] must reject jobs not assigned to the calling agent"))
                .WithMessage("*is not assigned to agent*");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// SubscribeToRun rejects an invalid (non-GUID) jobId — line 29 coverage.
    /// </summary>
    [Fact]
    public async Task SubscribeToRun_InvalidGuid_ThrowsHubException()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var connection = BuildUiConnection(serverAddress);
        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            Func<Task> act = () => connection.InvokeAsync("SubscribeToRun", "not-a-guid");

            await act.Should().ThrowAsync<Exception>(
                "SubscribeToRun must reject non-GUID jobId values (line 29 coverage)");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// UnsubscribeFromRun with a valid GUID removes the connection from the group — lines 72-77 coverage.
    /// </summary>
    [Fact]
    public async Task UnsubscribeFromRun_ValidGuid_Succeeds()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var jobId = Guid.NewGuid().ToString();
        var connection = BuildUiConnection(serverAddress);
        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            // Subscribe first so there is something to unsubscribe from
            await connection.InvokeAsync("SubscribeToRun", jobId);

            // Then unsubscribe — must not throw
            await connection.InvokeAsync("UnsubscribeFromRun", jobId);
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// UnsubscribeFromRun rejects an invalid GUID — line 74 coverage.
    /// </summary>
    [Fact]
    public async Task UnsubscribeFromRun_InvalidGuid_ThrowsHubException()
    {
        using var client = _factory.CreateClient();
        var serverAddress = _factory.ServerAddress;

        var connection = BuildUiConnection(serverAddress);
        try
        {
            await connection.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            Func<Task> act = () => connection.InvokeAsync("UnsubscribeFromRun", "not-a-valid-guid");

            await act.Should().ThrowAsync<Exception>(
                "UnsubscribeFromRun must reject non-GUID jobId values");
        }
        finally
        {
            await connection.StopAsync();
            await connection.DisposeAsync();
        }
    }



    /// <summary>
    /// Builds an agent-authenticated SignalR connection.
    /// Uses HMAC-SHA256(masterKey, agentId) derivation matching <see cref="AgentApiKeyAuthHandler"/>.
    /// </summary>
    private static HubConnection BuildAgentConnection(string serverAddress, string agentId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedToken = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        return new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}?agentId={agentId}&access_token={derivedToken}")
            .Build();
    }

    /// <summary>
    /// Builds an operator/UI-authenticated SignalR connection (no agentId param).
    /// Uses the master API key directly — this is the "operator" credential path.
    /// </summary>
    private static HubConnection BuildUiConnection(string serverAddress)
    {
        return new HubConnectionBuilder()
            .WithUrl($"{serverAddress}{HubRoutes.Agent}",
                options => options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(ApiWebApplicationFactory.ApiKey))
            .Build();
    }

    /// <summary>
    /// Seeds an in-memory <see cref="PipelineRun"/> into <see cref="IOrchestratorRunService"/>
    /// and sets the agent's <see cref="AgentEntry.ActiveJobId"/> + <see cref="AgentStatus.Busy"/>
    /// so the <c>[RequiresActiveJob]</c> authorization filter passes for the given jobId.
    ///
    /// Must be called AFTER the agent has called <c>RegisterAgent</c> on the hub, because
    /// <see cref="AgentRegistryService"/> only tracks agents that have connected.
    /// </summary>
    private static void SeedRunAndBusyAgent(AgentHubGateKestrelFactory factory, string agentId, string jobId)
    {
        var runService = factory.Services.GetRequiredService<IOrchestratorRunService>();
        var registry = factory.Services.GetRequiredService<AgentRegistryService>();

        // Add a PipelineRun to the in-memory run registry
        var run = new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueTitle = "Gate test run",
            IssueProviderConfigId = "test-prov",
            RepoProviderConfigId = "test-repo",
            AgentId = agentId,
            InitiatedBy = "gate-test",
        };
        runService.AddRun(run);

        // Set agent entry to Busy with this jobId so [RequiresActiveJob] passes
        var entry = registry.GetByAgentId(agentId);
        if (entry is not null)
        {
            entry.ActiveJobId = jobId;
            registry.TransitionStatus(agentId, AgentStatus.Busy);
        }
    }
}

/// <summary>
/// Kestrel factory for <see cref="AgentHubGateTests"/>.
/// Shared across all gate tests via xUnit collection fixture.
/// Follows the <see cref="ApiKestrelFactory"/> pattern from <see cref="HubAndDiTests"/>.
/// </summary>
public sealed class AgentHubGateKestrelFactory : WebApplicationFactory<Program>
{
    public AgentHubGateKestrelFactory()
    {
        UseKestrel(0); // random port
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

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();

        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
            services.RemoveAll<IHostedService>();

            RemoveDbContextRegistrations(services);
            var dbName = $"AgentHubGate-{Guid.NewGuid():N}";
            services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
                new GateTestDbContextFactory(dbName));

            services.RemoveAll<Infrastructure.Locking.IDistributedLockProvider>();
            services.AddDistributedLockProvider(null);

            services.RemoveAll<Infrastructure.IDatabaseProbe>();
            services.AddSingleton<Infrastructure.IDatabaseProbe>(new GateNoOpDatabaseProbe());

            services.RemoveAll<Pipeline.Interfaces.IProviderFactory>();
            services.AddSingleton(new Mock<Pipeline.Interfaces.IProviderFactory>().Object);

            services.RemoveAll<Pipeline.Interfaces.IQualityGateValidator>();
            services.AddSingleton(new Mock<Pipeline.Interfaces.IQualityGateValidator>().Object);

            services.RemoveAll<Pipeline.Interfaces.IConsolidationDispatchService>();
            services.AddSingleton<Pipeline.Interfaces.IConsolidationDispatchService>(
                new GateNoOpConsolidationDispatchService());
        });
    }

    protected override void Dispose(bool disposing)
    {
        // Note: do NOT clear env vars here — the shared ApiWebApplicationFactory in
        // ApiIntegrationTestCollection may still be building when this factory disposes.
        // Env vars are process-scoped and safe to leave set to their test values.
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

    private sealed class GateTestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly string _dbName;
        public GateTestDbContextFactory(string dbName) => _dbName = dbName;

        public PipelineDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new GatePipelineDbContext(options);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class GatePipelineDbContext : PipelineDbContext
    {
        public GatePipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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

    private sealed class GateNoOpDatabaseProbe : Infrastructure.IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

// ── No-op stubs ─────────────────────────────────────────────────────────────

file sealed class GateNoOpConsolidationDispatchService : CodingAgentWebUI.Pipeline.Interfaces.IConsolidationDispatchService
{
    public Task<CodingAgentWebUI.Pipeline.Interfaces.ConsolidationDispatchResult> TryDispatchAsync(Pipeline.Models.ConsolidationRun r, Pipeline.Models.ConsolidationRunType t,
        Pipeline.Models.TemplateId? tid, string? f, string w, CancellationToken ct)
        => Task.FromResult(CodingAgentWebUI.Pipeline.Interfaces.ConsolidationDispatchResult.Failed);
    public Task<bool> TryDispatchToAgentAsync(Pipeline.Models.RunId r, Pipeline.Models.ConsolidationRunType t, Pipeline.Models.TemplateId? tid,
        string w, Pipeline.Models.AgentId a, CancellationToken ct)
        => Task.FromResult(false);
    public Task NotifyRunCancelledAsync(Pipeline.Models.RunId r, CancellationToken ct) => Task.CompletedTask;
}
