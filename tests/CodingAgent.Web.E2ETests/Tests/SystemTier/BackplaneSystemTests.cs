using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.Infrastructure.SystemTier;
using CodingAgent.Web.TestUtilities;
using k8s.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Web.E2ETests.Tests.SystemTier;

/// <summary>
/// Tests that SignalR output delivery and chat prompts work correctly across two API replicas
/// that share the Redis backplane. These tests cover the real production path that the fake-Redis
/// multi-replica tests in <see cref="MultiReplicaTests"/> cannot exercise:
/// <see cref="MultiReplicaTests"/> comments explicitly note that chat prompt delivery is not
/// tested there because there is no SignalR backplane.
/// </summary>
[Collection(SystemE2ECollection.Name)]
[Trait("Category", "System")]
public sealed class BackplaneSystemTests(SystemContainers containers) : SystemE2ETestBase(containers)
{
    /// <summary>
    /// Connects a UI-side <see cref="HubConnection"/> to <paramref name="api"/>'s
    /// <c>/hubs/ui</c> endpoint using an admin user token and the JSON protocol.
    /// The UI hub refuses MessagePack, so <c>AddMessagePackProtocol</c> must not be called.
    /// </summary>
    private static async Task<HubConnection> ConnectUiAsync(SystemApiFactory api)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri($"{api.ServerAddress}{HubRoutes.Ui}"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(CreateAdminUserToken());
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    /// <summary>
    /// Agent on replica A reports output; UI subscribed to replica B receives it via the
    /// Redis SignalR backplane. Removing <c>AddStackExchangeRedis</c> from
    /// <c>ApiSignalRRegistration</c> makes this test fail.
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenAgentOnReplicaAReportsOutput_UiOnReplicaBShouldReceiveIt()
    {
        // Arrange — two independent API replicas sharing the same Redis container.
        var a = await StartApiAsync();
        var b = await StartApiAsync();

        var agentId = $"system-agent-{Guid.NewGuid():N}";
        var issue = $"system-{Guid.NewGuid():N}";
        var id = await SeedRunningWorkItemAsync(a, issue, agentId);

        await using var agent = new FakeAgentClient(agentId, "e2e");
        await agent.ConnectWithActiveJobAsync(
            a.ServerAddress, E2EWebApplicationFactory.TestApiKey, id.ToString(), issue, "repo-e2e");

        // Connect the UI to replica B and register the handler *before* subscribing,
        // because SubscribeToRun sends the backlog synchronously during the call.
        await using var ui = await ConnectUiAsync(b);

        var receivedLines = new ConcurrentQueue<string>();
        ui.On<string, IReadOnlyList<string>>(HubMethodNames.OnOutputLines, (runId, lines) =>
        {
            if (runId == id.ToString())
                foreach (var line in lines)
                    receivedLines.Enqueue(line);
        });

        // SubscribeToRun awaits AddToGroupAsync before returning, so reporting output
        // after this call guarantees the UI is in the run group on replica B.
        await ui.InvokeAsync(HubMethodNames.SubscribeToRun, id.ToString());

        // Act — agent on replica A reports a line.
        await agent.ReportOutputAsync(id.ToString(), "line-from-replica-a");

        // Assert — the line must arrive on replica B via the backplane.
        await WaitUntilAsync(
            () => Task.FromResult(receivedLines.Contains("line-from-replica-a")),
            TimeSpan.FromSeconds(15),
            "UI on replica B should receive output reported by agent on replica A via the Redis backplane");
    }

    /// <summary>
    /// Agent reports output before the UI connects; the UI subscribes to replica B after the
    /// fact and receives the backlog from the shared Redis ring buffer.
    /// This test passes even without the SignalR backplane (backlog is Redis storage, not
    /// SignalR pub/sub), which is the expected mutation-check behaviour.
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenUiSubscribesOnReplicaBAfterOutput_ShouldReceiveBacklogFromRedis()
    {
        // Arrange
        var a = await StartApiAsync();
        var b = await StartApiAsync();

        var agentId = $"system-agent-{Guid.NewGuid():N}";
        var issue = $"system-{Guid.NewGuid():N}";
        var id = await SeedRunningWorkItemAsync(a, issue, agentId);

        await using var agent = new FakeAgentClient(agentId, "e2e");
        await agent.ConnectWithActiveJobAsync(
            a.ServerAddress, E2EWebApplicationFactory.TestApiKey, id.ToString(), issue, "repo-e2e");

        // Output is reported *before* the UI connects. AppendOutputLines is fire-and-forget
        // (DistributedRunService discards the AppendOutputToRedisAsync task), so the Redis
        // RPUSH may still be in-flight when this line returns. Poll the ring buffer directly
        // until the write commits; if SubscribeToRun were called while the buffer is empty it
        // would push nothing and the line would never arrive (WaitUntilAsync cannot rescue that
        // because it only checks receivedLines, not the buffer).
        await agent.ReportOutputAsync(id.ToString(), "earlier-line");

        await WaitUntilAsync(
            async () =>
            {
                var backlog = await a.Services.GetRequiredService<IOrchestratorRunService>()
                    .GetOutputBacklogAsync(new RunId(id.ToString()));
                return backlog.Contains("earlier-line");
            },
            TimeSpan.FromSeconds(10),
            "earlier-line should appear in the Redis ring buffer before the UI subscribes");

        // Act — late-joining UI connects to replica B and subscribes.
        await using var ui = await ConnectUiAsync(b);

        var receivedLines = new ConcurrentQueue<string>();
        // Register the handler before SubscribeToRun: the subscribe call sends the backlog
        // synchronously to the caller connection.
        ui.On<string, IReadOnlyList<string>>(HubMethodNames.OnOutputLines, (runId, lines) =>
        {
            if (runId == id.ToString())
                foreach (var line in lines)
                    receivedLines.Enqueue(line);
        });

        await ui.InvokeAsync(HubMethodNames.SubscribeToRun, id.ToString());

        // Assert — the backlog entry must be delivered from shared Redis.
        await WaitUntilAsync(
            () => Task.FromResult(receivedLines.Contains("earlier-line")),
            TimeSpan.FromSeconds(15),
            "UI on replica B should receive the backlog from the shared Redis ring buffer");
    }

    /// <summary>
    /// Chat pod connects to replica A; the POST to start the session and all prompt/reply traffic
    /// runs through replica B — the full round-trip crosses the Redis backplane in both directions.
    /// Removing <c>AddStackExchangeRedis</c> from <c>ApiSignalRRegistration</c> makes this test fail.
    /// </summary>
    [RequiresDockerFact]
    public async Task WhenChatPodIsOnReplicaA_PromptAndReplyShouldCrossToReplicaB()
    {
        // Arrange — two replicas.
        var a = await StartApiAsync();
        var b = await StartApiAsync();

        // Seed agent profile and routing into b's real Postgres stores so b's chat dispatcher
        // can resolve the "system-chat" profile. The "e2e" job template uses providerType
        // "opencode" — no PVC is claimed, which is required in the System tier.
        await b.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(
            AgentRoutingTestData.Profile("system-chat", jobTemplate: "e2e"),
            CancellationToken.None);
        await AgentRoutingTestData.SeedRoutingAsync(
            b.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            AgentRoutingTestData.Routing("system-chat"));

        // Subscribe to ChatJobCreated *before* posting to avoid a race: FakeKubernetesJobClient
        // fires the event synchronously inside CreateJobAsync.
        var chatJobTcs = new TaskCompletionSource<V1Job>();
        b.Kubernetes.ChatJobCreated += job => chatJobTcs.TrySetResult(job);

        // Act — start: fire POST /api/ui/chat on b but do not await it yet.
        // The endpoint blocks until an agent with the matching caa/chat-session-id connects;
        // awaiting before the agent connects would deadlock.
        using var uiClient = CreateUiClient(b);
        var startTask = uiClient.PostAsJsonAsync("/api/ui/chat", new UiChatStartRequest("system-chat", null));

        // Wait for the K8s job to appear so we can read the dispatch ID from its label.
        var chatJob = await chatJobTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dispatchId = chatJob.Metadata.Labels["caa/chat-session-id"];

        // Connect the fake agent to replica A — this is the cross-replica scenario.
        // ConnectAsChatAgentAsync adds chat=true and chat-session-id=<dispatchId> to the labels,
        // mirroring AgentConnectionLifecycle. The shared Redis registry lets b's dispatcher
        // find the agent even though it is connected to a.
        await using var agent = new FakeAgentClient($"system-chat-{Guid.NewGuid():N}", "e2e");
        await agent.ConnectAsChatAgentAsync(a.ServerAddress, E2EWebApplicationFactory.TestApiKey, dispatchId);

        // Await the POST now that the agent has connected.
        var startResponse = await startTask.WaitAsync(TimeSpan.FromSeconds(60));

        // Assert — session
        startResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "POST /api/ui/chat should return 200 with a UiChatSession once the agent connects");
        var session = await startResponse.Content.ReadFromJsonAsync<UiChatSession>();
        session.Should().NotBeNull();
        session!.AgentId.Should().Be(agent.AgentId,
            "the session AgentId should match the fake agent that connected to replica A");

        // Act — chat: connect UI to b, register the OnChatResponse handler *before* subscribing
        // to the chat group, then send a prompt through b.
        await using var ui = await ConnectUiAsync(b);

        var chatResponses = new ConcurrentQueue<(string SessionId, IReadOnlyList<string> Lines)>();
        ui.On<string, IReadOnlyList<string>>(HubMethodNames.OnChatResponse, (sessionId, lines) =>
            chatResponses.Enqueue((sessionId, lines)));

        await ui.InvokeAsync(HubMethodNames.SubscribeToChat, session.AgentId);

        var promptResponse = await uiClient.PostAsJsonAsync(
            $"/api/ui/chat/{session.AgentId}/prompt",
            new UiChatPromptRequest("system-session", "hello"));

        // Assert — prompt arrived at the agent on replica A via the backplane.
        // ChatService calls hub.Clients.Client(entry.ConnectionId).AssignChatPrompt(message);
        // the backplane delivers this direct-client message to the connection on replica A.
        promptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "POST .../prompt should return 204");

        var chatPrompt = await agent.ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        chatPrompt.SessionId.Should().Be("system-session",
            "the prompt should arrive at the agent on replica A with the correct session ID");

        // Act — reply: RespondToChatAsync reads SessionId from ChatPromptAssigned (already
        // resolved above) and invokes ReportChatResponse + ReportChatCompleted on replica A.
        // AgentHub.Chat pushes OnChatResponse to the chat group; the backplane delivers it to
        // the UI connection on replica B.
        await agent.RespondToChatAsync("reply-from-replica-a");

        // Assert — reply received on the UI connected to replica B.
        await WaitUntilAsync(
            () => Task.FromResult(chatResponses.Any(r =>
                r.SessionId == "system-session" &&
                r.Lines.Contains("reply-from-replica-a"))),
            TimeSpan.FromSeconds(15),
            "UI on replica B should receive the chat response sent by the agent on replica A");
    }
}
