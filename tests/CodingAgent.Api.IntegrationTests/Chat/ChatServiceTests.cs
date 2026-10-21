using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Api.Chat;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Contracts;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.Chat;

/// <summary>
/// Spec 051 2.5b and spec 053 req 6: the chat rules the API applies. Which profiles can be launched and with which
/// models and efforts, what a launch dispatches, how refusals and failures read, and what each prompt carries.
/// UiChatEndpointTests cover access and ownership.
/// </summary>
public sealed class ChatServiceTests
{
    private const string ProfileId = "chat-profile";
    private const string AgentId = "chat-agent-1";
    private const string ConnectionId = "conn-chat-agent-1";
    private const string DefaultModel = "kiro-model-a";

    private readonly Mock<IAgentProfileStore> _profiles = new();
    private readonly Mock<IProjectStore> _projects = new();
    private readonly Mock<IChatJobDispatcher> _dispatcher = new();
    private readonly Mock<IAgentRegistryService> _registry = new();
    private readonly Mock<IAgentHubClient> _agent = new();
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());
    private readonly ChatSessionStore _sessions = new(null, TimeSpan.FromHours(1));
    private readonly List<ChatPromptMessage> _delivered = [];
    private readonly ChatService _sut;

    public ChatServiceTests()
    {
        UseProfiles(Profile(ProfileId, "dotnet,kiro"));
        AgentRoutingTestSupport.SeedRouting(_db, AgentRoutingTestData.Routing(ProfileId, DefaultModel, "high"));
        _dispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentId);
        _registry.Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentEntry { AgentId = new AgentId(AgentId), ConnectionId = ConnectionId, Hostname = "pod", Labels = ["kiro"], RegisteredAt = DateTimeOffset.UtcNow });
        _agent.Setup(a => a.AssignChatPrompt(It.IsAny<ChatPromptMessage>()))
            .Callback<ChatPromptMessage>(_delivered.Add)
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients<IAgentHubClient>>();
        clients.Setup(c => c.Client(ConnectionId)).Returns(_agent.Object);
        var hub = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        _sut = new ChatService(
            _profiles.Object, new AgentRoutingStore(_db), _projects.Object,
            AgentRoutingTestData.JobTemplates(
                ("dotnet,kiro", JobTemplateProviderType.Kiro),
                ("claude,dotnet", JobTemplateProviderType.Claude),
                ("java,opencode", JobTemplateProviderType.Opencode),
                ("dotnet,python", "")),
            _dispatcher.Object, _sessions, _registry.Object, hub.Object,
            new DispatchServiceOptions { ChatPodConnectTimeoutSeconds = 90 },
            NullLogger<ChatService>.Instance);
    }

    private static AgentProfile Profile(
        string id, string jobTemplate, bool enabled = true, IReadOnlyDictionary<string, string>? settings = null) =>
        AgentRoutingTestData.Profile(id, jobTemplate, $"{id} profile", enabled, settings);

    private void UseProfiles(params AgentProfile[] profiles) =>
        _profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(profiles);

    private async Task UseRoutingAsync(RoutingTable routing) => await new AgentRoutingStore(_db).SaveAsync(routing, CancellationToken.None);

    private static ChatSessionRecord Session(string? projectId = null, bool prompted = false, string profileId = ProfileId) =>
        new(AgentId, "oidc", "oidc:tester", projectId, profileId, "window-1", prompted);

    private Task<UiChatSession> StartAsync(
        string profileId = ProfileId, string? model = null, string? effort = null, PipelineProject? project = null,
        IReadOnlyList<PipelineJobTemplate>? chatTemplates = null) =>
        _sut.StartAsync(
            new UiChatStartRequest(profileId, project?.Id, model, effort), project, chatTemplates ?? [],
            "oidc", "oidc:tester", CancellationToken.None);

    // ── Options ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Options_OfferOnlyEnabledProfilesWhoseJobTemplateHasATool()
    {
        UseProfiles(
            Profile("kiro", "dotnet,kiro"),
            Profile("claude-disabled", "claude,dotnet", enabled: false),
            Profile("no-template", "java,spring"),
            Profile("no-tool", "dotnet,python"));

        var options = await _sut.GetOptionsAsync([], CancellationToken.None);

        options.Profiles.Select(p => p.Id).Should().Equal("kiro");
        options.ConnectTimeoutSeconds.Should().Be(90);
    }

    [Fact]
    public async Task Options_ListTheModelsAndEffortsOfEachTool()
    {
        UseProfiles(Profile("kiro", "dotnet,kiro"), Profile("claude", "claude,dotnet"), Profile("opencode", "java,opencode"));
        await UseRoutingAsync(AgentRoutingTestData.Routing(
            "kiro", DefaultModel, "auto",
            (WorkItemTaskType.Review, AgentRoutingTestData.Route("claude", "claude-row-model")),
            (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("opencode", "openai/gpt-row"))));

        var options = (await _sut.GetOptionsAsync([], CancellationToken.None)).Profiles.ToDictionary(p => p.Id);

        options["kiro"].Should().BeEquivalentTo(new
        {
            Tool = JobTemplateProviderType.Kiro,
            Models = new[] { "auto", DefaultModel },
            Efforts = new[] { "auto" }.Concat(AgentToolCatalog.Kiro.Efforts),
        });
        options["claude"].Models.Should().Equal(new[] { "auto", "claude-row-model" }.Concat(AgentToolCatalog.ClaudeCode.BuiltInModels),
            "Claude Code also offers its built-in models");
        options["claude"].Efforts.Should().Equal(new[] { "auto" }.Concat(AgentToolCatalog.ClaudeCode.Efforts));
        options["opencode"].Models.Should().Equal("auto", "openai/gpt-row");
        options["opencode"].Efforts.Should().Equal(["auto"], "OpenCode has no effort");
    }

    [Fact]
    public async Task Options_OfferTheChatProjectsRows_ButNoOtherProjectsModels()
    {
        var ownTemplate = new PipelineJobTemplate
        {
            Id = "t-own", Name = "own", IssueProviderId = "ip", RepoProviderId = "rp",
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Implementation] = AgentRoutingTestData.Route(ProfileId, "own-project-model") },
        };

        // Another project's template, with "other-project-model" on its row, is not among the chat's templates.
        var options = await _sut.GetOptionsAsync([ownTemplate], CancellationToken.None);

        options.Profiles.Single().Models.Should().Equal("auto", DefaultModel, "own-project-model");
    }

    [Fact]
    public async Task Options_PreselectTheDefaultRoute_WhenItNamesTheProfile()
    {
        UseProfiles(Profile(ProfileId, "dotnet,kiro"), Profile("claude", "claude,dotnet"));

        var options = (await _sut.GetOptionsAsync([], CancellationToken.None)).Profiles.ToDictionary(p => p.Id);

        options[ProfileId].Should().BeEquivalentTo(new { DefaultModel, DefaultEffort = "high" });
        options["claude"].Should().BeEquivalentTo(new { DefaultModel = "auto", DefaultEffort = "auto" });
    }

    // ── Launch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_DispatchesTheProfilesJobTemplate_WithTheChosenModelAndEffort_AndRecordsTheOwner()
    {
        var project = new PipelineProject { Id = "6f1c2a9e-0000-0000-0000-0000000000f1", Name = "Foxtrot" };

        var session = await StartAsync(model: DefaultModel, effort: "low", project: project);

        session.Should().Be(new UiChatSession(AgentId, DefaultModel));
        _dispatcher.Verify(d => d.DispatchChatPodAsync("dotnet,kiro", DefaultModel, "low", It.IsAny<CancellationToken>()), Times.Once);
        (await _sessions.FindAsync(AgentId)).Should().BeEquivalentTo(new
        {
            AgentId, OwnerIdentitySource = "oidc", OwnerSubject = "oidc:tester", ProjectId = project.Id, ProfileId,
            Prompted = false, Model = DefaultModel, Effort = "low",
        });
    }

    [Fact]
    public async Task Start_WithoutAModelOrEffort_UsesThePreselection()
    {
        (await StartAsync()).Model.Should().Be(DefaultModel);
        _dispatcher.Verify(d => d.DispatchChatPodAsync("dotnet,kiro", DefaultModel, "high", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_WithoutADefaultRouteForTheProfile_RunsWithAuto()
    {
        await UseRoutingAsync(RoutingTable.Empty);

        (await StartAsync()).Model.Should().Be("auto");
        _dispatcher.Verify(d => d.DispatchChatPodAsync("dotnet,kiro", "auto", "auto", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_AProfileThatCannotBeLaunched_IsRefusedWith400WithoutDispatching()
    {
        UseProfiles(Profile(ProfileId, "dotnet,kiro"), Profile("disabled", "dotnet,kiro", enabled: false), Profile("no-tool", "dotnet,python"));

        foreach (var profileId in new[] { "gone", "disabled", "no-tool" })
        {
            var act = () => StartAsync(profileId);

            (await act.Should().ThrowAsync<ChatRequestException>())
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest, profileId);
        }
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Start_AModelTheOptionsDoNotOffer_IsRefusedWith400WithoutDispatching()
    {
        var act = () => StartAsync(model: "--dangerously-skip-permissions");

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.Should().BeEquivalentTo(new
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Message = "Model '--dangerously-skip-permissions' is not offered for agent profile 'chat-profile profile'. Choose one from the list.",
            });
        _dispatcher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProfileId, "xhigh", "Kiro does not accept effort 'xhigh'.")]
    [InlineData("opencode", "high", "OpenCode does not accept effort 'high'.")]
    public async Task Start_AnEffortTheToolDoesNotAccept_IsRefusedWith400WithoutDispatching(string profileId, string effort, string message)
    {
        UseProfiles(Profile(ProfileId, "dotnet,kiro"), Profile("opencode", "java,opencode"));

        var act = () => StartAsync(profileId, model: "auto", effort: effort);

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.Should().BeEquivalentTo(new { StatusCode = StatusCodes.Status400BadRequest, Message = message });
        _dispatcher.VerifyNoOtherCalls();
    }

    public static TheoryData<Exception, string> LaunchFailures() => new()
    {
        { new NoPvcAvailableException(), "No agent credentials available. Try again later." },
        { new ChatPodTimeoutException(120), "Pod did not connect within 120s. Check cluster logs." },
        { new InvalidOperationException("No template for selector 'kiro'"), "Launch failed: No template for selector 'kiro'" },
    };

    [Theory]
    [MemberData(nameof(LaunchFailures))]
    public async Task Start_APodThatCannotBeStarted_Answers502WithTheReason(Exception failure, string reason)
    {
        _dispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var act = () => StartAsync();

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.Should().BeEquivalentTo(new { StatusCode = StatusCodes.Status502BadGateway, Message = reason });
        (await _sessions.FindAsync(AgentId)).Should().BeNull();
    }

    // ── Prompts ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("java,opencode", null, "/home/ubuntu/.opencode/opencode.json")]
    [InlineData("dotnet,kiro", null, "/home/ubuntu/.kiro/settings/mcp.json")]
    [InlineData("claude,dotnet", null, AgentDefaults.ClaudeMcpConfigPath)]
    [InlineData("java,opencode", "/custom/mcp.json", "/custom/mcp.json")]
    public async Task Prompt_CarriesTheMcpConfigPath_OfTheProfilesTool(string jobTemplate, string? explicitPath, string expected)
    {
        UseProfiles(Profile(ProfileId, jobTemplate,
            settings: explicitPath is null ? null : new Dictionary<string, string> { [ProviderSettingKeys.McpConfigPath] = explicitPath }));

        await _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);

        _delivered.Single().McpConfigPath.Should().Be(expected);
    }

    [Fact]
    public async Task Prompt_CarriesTheProfilesAuthMode()
    {
        UseProfiles(Profile(ProfileId, "claude,dotnet",
            settings: new Dictionary<string, string> { [ProviderSettingKeys.AuthMode] = "api-key" }));

        await _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);

        _delivered.Single().AgentAuthMode.Should().Be("api-key");
    }

    [Fact]
    public async Task Prompt_ForAProfileThatCanNoLongerRun_IsRefused()
    {
        UseProfiles(Profile(ProfileId, "dotnet,python"));

        var act = () => _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstPrompt_CarriesTheProjectsSecretsAndServers_AndLaterOnesResume()
    {
        var project = new PipelineProject
        {
            Id = "6f1c2a9e-0000-0000-0000-0000000000f2", Name = "Golf", Secrets = new() { ["TOKEN"] = "s3cret" }, SteeringContent = "be brief",
            McpServers = [new McpServerConfig { Name = "docs", Command = "docs-mcp" }],
        };
        _projects.Setup(p => p.GetProjectByIdAsync(project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        await _sessions.SaveAsync(Session(project.Id));

        await _sut.SendPromptAsync((await _sessions.FindAsync(AgentId))!, new UiChatPromptRequest("s1", "first"), CancellationToken.None);
        await _sut.SendPromptAsync((await _sessions.FindAsync(AgentId))!, new UiChatPromptRequest("s2", "second"), CancellationToken.None);

        _delivered[0].Should().BeEquivalentTo(new
        {
            SessionId = "s1", Prompt = "first", UseResume = false, ChatWindowId = "window-1",
            ProjectSecrets = project.Secrets, ProjectSteeringContent = "be brief", ProjectId = project.Id, ProjectName = "Golf",
        });
        _delivered[0].McpServers.Select(s => s.Name).Should().Equal("docs");
        _delivered[1].Should().BeEquivalentTo(new { SessionId = "s2", UseResume = true, ProjectSecrets = (object?)null, ProjectId = (string?)null });
    }

    [Fact]
    public async Task Prompt_StampsTheSessionOnTheAgent_BeforeDeliveringIt()
    {
        var order = new List<string>();
        _registry.Setup(r => r.UpdateAgentFieldAsync(It.IsAny<AgentId>(), AgentFieldNames.ActiveChatSessionId, "s1"))
            .Callback(() => order.Add("stamp")).Returns(Task.CompletedTask);
        _agent.Setup(a => a.AssignChatPrompt(It.IsAny<ChatPromptMessage>())).Callback(() => order.Add("deliver")).Returns(Task.CompletedTask);

        await _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);

        order.Should().Equal("stamp", "deliver");
    }

    [Fact]
    public async Task Prompt_ToADisconnectedOrMissingPod_IsRefused()
    {
        _registry.Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentEntry { AgentId = new AgentId(AgentId), ConnectionId = ConnectionId, Hostname = "pod", Labels = [], RegisteredAt = DateTimeOffset.UtcNow, Status = AgentStatus.Disconnected });
        var disconnected = () => _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);
        (await disconnected.Should().ThrowAsync<ChatRequestException>()).WithMessage("The chat pod is disconnected.");

        _registry.Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentEntry?)null);
        var missing = () => _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);
        (await missing.Should().ThrowAsync<ChatRequestException>()).WithMessage("The chat pod is gone*");

        _delivered.Should().BeEmpty();
    }

    // ── End ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Terminate_StopsThePod_AndForgetsTheSession()
    {
        await _sessions.SaveAsync(Session());

        await _sut.TerminateAsync(Session(), CancellationToken.None);

        _dispatcher.Verify(d => d.TerminateChatSessionAsync(new AgentId(AgentId), It.IsAny<CancellationToken>()), Times.Once);
        (await _sessions.FindAsync(AgentId)).Should().BeNull();
    }
}
