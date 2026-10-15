using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Api.Chat;
using CodingAgent.Contracts.Auth;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 T5: the UI hub at <c>/hubs/ui</c> against a real Kestrel API, connected with the web's own
/// <see cref="UiHubConnection"/> (WebSockets, no negotiation). Only user tokens connect; a run is watched only
/// by readers of its project and a chat only by its owner, and nothing of a refused group arrives. The
/// connection closes when its token expires. <c>/hubs/agent</c> is for agents only.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiHubTests(ApiKestrelFactory api) : IClassFixture<ApiKestrelFactory>
{
    private const string ProjectT = "6f1c2a9e-0000-0000-0000-0000000000d1";
    private const string ProjectU = "6f1c2a9e-0000-0000-0000-0000000000d2";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private Uri HubUrl
    {
        get
        {
            api.CreateClient().Dispose(); // starts the host
            return new Uri($"{api.ServerAddress}{HubRoutes.Ui}");
        }
    }

    private sealed class FixedToken(string? token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult(token);
    }

    private async Task<UiHubConnection> ConnectAsync(string? token)
    {
        var connection = new UiHubConnection(HubUrl, new FixedToken(token));
        using var cts = new CancellationTokenSource(Wait);
        await connection.StartAsync(cts.Token);
        return connection;
    }

    private Guid SeedRun(string projectId)
    {
        using var db = api.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>().CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "42",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Running,
            Payload = "{}",
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            ProjectId = Guid.Parse(projectId),
        };
        db.WorkItems.Add(entity);
        db.SaveChanges();
        return entity.Id;
    }

    private IHubClients UiClients => api.Services.GetRequiredService<IHubContext<UiHub>>().Clients;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue("the condition should hold within {0}", Wait);
    }

    [Fact]
    public async Task UserToken_ConnectsOverWebSocketsWithoutNegotiation()
    {
        await using var connection = await ConnectAsync(TestUserTokens.For(TestUserTokens.Global(AccessRole.ReadOnly)));

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task NoToken_AndTheApiKey_AreRefused()
    {
        await FluentActions.Awaiting(() => ConnectAsync(null)).Should().ThrowAsync<Exception>();
        await FluentActions.Awaiting(() => ConnectAsync(ApiWebApplicationFactory.ApiKey)).Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task AgentHub_RefusesUserTokens_AndOffersTheMasterKeyNoSubscription()
    {
        var userTokenConnection = new HubConnectionBuilder()
            .WithUrl($"{api.ServerAddress}{HubRoutes.Agent}?agentId=a1", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(TestUserTokens.For(TestUserTokens.Global(AccessRole.Admin)));
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        await using (userTokenConnection)
            await FluentActions.Awaiting(() => userTokenConnection.StartAsync()).Should().ThrowAsync<Exception>();

        // The master key without an agent ID was the web's operator connection. The hub now drops it, and
        // the subscribe methods are gone from it either way.
        var masterKeyConnection = new HubConnectionBuilder()
            .WithUrl($"{api.ServerAddress}{HubRoutes.Agent}", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(ApiWebApplicationFactory.ApiKey);
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        await using (masterKeyConnection)
        {
            try { await masterKeyConnection.StartAsync(); }
            catch (Exception) { return; }
            await FluentActions.Awaiting(() => masterKeyConnection.InvokeAsync(HubMethodNames.SubscribeToRun, Guid.NewGuid().ToString()))
                .Should().ThrowAsync<Exception>();
        }
    }

    [Fact]
    public async Task SubscribeToRun_OfAnInvisibleOrUnknownRun_IsRefused_AndOnlyTheVisibleRunsEventsArrive()
    {
        var visible = SeedRun(ProjectT).ToString();
        var invisible = SeedRun(ProjectU).ToString();
        await using var connection = await ConnectAsync(TestUserTokens.For(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)));
        var received = new ConcurrentQueue<string>();
        connection.On<string, IReadOnlyList<string>>(HubMethodNames.OnOutputLines, (runId, _) => received.Enqueue(runId));

        await FluentActions.Awaiting(() => connection.InvokeAsync(HubMethodNames.SubscribeToRun, invisible))
            .Should().ThrowAsync<HubException>();
        await FluentActions.Awaiting(() => connection.InvokeAsync(HubMethodNames.SubscribeToRun, Guid.NewGuid().ToString()))
            .Should().ThrowAsync<HubException>();
        await connection.InvokeAsync(HubMethodNames.SubscribeToRun, visible);

        await UiClients.Group(UiHubGroups.Run(invisible)).SendAsync(HubMethodNames.OnOutputLines, invisible, new[] { "secret" });
        await UiClients.Group(UiHubGroups.Run(visible)).SendAsync(HubMethodNames.OnOutputLines, visible, new[] { "hello" });

        await WaitUntilAsync(() => received.Contains(visible));
        received.Should().Equal(visible);
    }

    [Fact]
    public async Task SubscribeToChat_NeedsTheChatsOwner()
    {
        const string agentId = "caa-chat-t5";
        await api.Services.GetRequiredService<ChatSessionStore>().SaveAsync(
            new ChatSessionRecord(agentId, "oidc", "oidc:anna", ProjectT, "profile", "window"));
        var operatorOfT = TestUserTokens.Scoped(ProjectT, AccessRole.Operator);

        await using (var bert = await ConnectAsync(TestUserTokens.For(operatorOfT, "oidc:bert", "bert")))
        {
            await FluentActions.Awaiting(() => bert.InvokeAsync(HubMethodNames.SubscribeToChat, agentId))
                .Should().ThrowAsync<HubException>("another operator of the project does not see Anna's chat");
        }

        await using var anna = await ConnectAsync(TestUserTokens.For(operatorOfT, "oidc:anna", "anna"));
        var lines = new ConcurrentQueue<string>();
        var completed = new ConcurrentQueue<(string SessionId, int ExitCode, string? Error)>();
        anna.On<string, IReadOnlyList<string>>(HubMethodNames.OnChatResponse, (_, l) => { foreach (var line in l) lines.Enqueue(line); });
        anna.On<string, int, string?>(HubMethodNames.OnChatCompleted, (s, code, error) => completed.Enqueue((s, code, error)));
        await anna.InvokeAsync(HubMethodNames.SubscribeToChat, agentId);

        await UiClients.Group(UiHubGroups.Chat(agentId)).SendAsync(HubMethodNames.OnChatResponse, "s1", new[] { "hi", "there" });
        await UiClients.Group(UiHubGroups.Chat(agentId)).SendAsync(HubMethodNames.OnChatCompleted, "s1", 1, "boom");

        await WaitUntilAsync(() => !completed.IsEmpty);
        lines.Should().Equal("hi", "there");
        completed.Should().Equal(("s1", 1, "boom"));
    }

    /// <summary>
    /// Every event the hub pushes arrives intact through the JSON protocol, the only one the UI hub speaks
    /// (the strongly-typed IDs have only MessagePack formatters).
    /// </summary>
    [Fact]
    public async Task EveryRunEvent_SurvivesTheJsonProtocol()
    {
        var run = SeedRun(ProjectT).ToString();
        await using var connection = await ConnectAsync(TestUserTokens.For(TestUserTokens.Global(AccessRole.ReadOnly)));
        var received = new ConcurrentDictionary<string, object?>();
        connection.On<string, IReadOnlyList<string>>(HubMethodNames.OnOutputLines, (_, v) => received[HubMethodNames.OnOutputLines] = v);
        connection.On<string, PipelineStep, DateTimeOffset>(HubMethodNames.OnStepTransition, (_, s, t) => received[HubMethodNames.OnStepTransition] = (s, t));
        connection.On<string, JobCompletionPayload>(HubMethodNames.OnRunCompleted, (_, v) => received[HubMethodNames.OnRunCompleted] = v);
        connection.On<string, ChatRole, string>(HubMethodNames.OnChatEntry, (_, r, c) => received[HubMethodNames.OnChatEntry] = (r, c));
        connection.On<string, QualityGateReport>(HubMethodNames.OnQualityGateResult, (_, v) => received[HubMethodNames.OnQualityGateResult] = v);
        connection.On<string, bool, int>(HubMethodNames.OnBrainSyncResult, (_, l, n) => received[HubMethodNames.OnBrainSyncResult] = (l, n));
        connection.On<string, RunStateSnapshot>(HubMethodNames.OnRunStateSnapshot, (_, v) => received[HubMethodNames.OnRunStateSnapshot] = v);
        await connection.InvokeAsync(HubMethodNames.SubscribeToRun, run);

        var at = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(2));
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "build", Passed = true, Details = "ok" },
            Tests = new GateResult { GateName = "tests", Passed = false, Details = "2 failed" },
            QgcResults = [new QgcExecutionResult { QgcId = "q1", DisplayName = "dotnet", Tests = new GateResult { GateName = "t", Passed = true } }],
            Timestamp = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc),
        };
        var completion = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = at,
            PullRequestUrl = "https://example.test/pr/7",
            FailureCategory = FailureReason.AgentError,
            TotalCost = 1.25m,
            CodeReviewAgentsRun = ["security"],
        };
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.RunningQualityGates,
            HighWaterMark = PipelineStep.RunningQualityGates,
            IssueLabels = ["agent:next"],
            LatestQualityReport = report,
            QualityGateHistory = [report],
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = at,
            ChatHistory = [new ChatEntry { Role = ChatRole.Agent, Content = "working", Timestamp = at }],
        };
        var group = UiClients.Group(UiHubGroups.Run(run));
        await group.SendAsync(HubMethodNames.OnOutputLines, run, new[] { "a", "b" });
        await group.SendAsync(HubMethodNames.OnStepTransition, run, PipelineStep.RunningQualityGates, at);
        await group.SendAsync(HubMethodNames.OnRunCompleted, run, completion);
        await group.SendAsync(HubMethodNames.OnChatEntry, run, ChatRole.User, "hi");
        await group.SendAsync(HubMethodNames.OnQualityGateResult, run, report);
        await group.SendAsync(HubMethodNames.OnBrainSyncResult, run, true, 3);
        await group.SendAsync(HubMethodNames.OnRunStateSnapshot, run, snapshot);

        await WaitUntilAsync(() => received.Count == 7);
        received[HubMethodNames.OnOutputLines].Should().BeEquivalentTo(new[] { "a", "b" });
        received[HubMethodNames.OnStepTransition].Should().Be((PipelineStep.RunningQualityGates, at));
        received[HubMethodNames.OnRunCompleted].Should().BeEquivalentTo(completion);
        received[HubMethodNames.OnChatEntry].Should().Be((ChatRole.User, "hi"));
        received[HubMethodNames.OnQualityGateResult].Should().BeEquivalentTo(report);
        received[HubMethodNames.OnBrainSyncResult].Should().Be((true, 3));
        received[HubMethodNames.OnRunStateSnapshot].Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task Connection_ClosesWhenItsToken_AndSoTheSession_Expires()
    {
        var grant = TestUserTokens.Global(AccessRole.ReadOnly);
        var closed = new TaskCompletionSource();
        var connection = new HubConnectionBuilder()
            .WithUrl(HubUrl, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(TestUserTokens.ValidUntil(grant, DateTimeOffset.UtcNow.AddSeconds(3)));
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await using (connection)
        {
            await connection.StartAsync();

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        // Signing in again gives a fresh token, and the hub takes it.
        await using var again = await ConnectAsync(TestUserTokens.For(grant));
        again.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public void UiHub_ExposesOnlyTheSubscribeMethods()
    {
        typeof(UiHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Should().BeEquivalentTo(
                HubMethodNames.SubscribeToRun, HubMethodNames.UnsubscribeFromRun,
                HubMethodNames.SubscribeToChat, HubMethodNames.UnsubscribeFromChat);
    }
}
