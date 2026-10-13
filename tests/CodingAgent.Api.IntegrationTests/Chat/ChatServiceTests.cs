using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Api.Chat;
using CodingAgent.Contracts;
using CodingAgent.Contracts.Ui;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Chat;

/// <summary>
/// Spec 051 2.5b: the chat rules the Agent Chat page applied until they moved into the API. Which
/// profiles can be launched, what a launch dispatches, how failures read, and what each prompt carries.
/// UiChatEndpointTests cover access and ownership.
/// </summary>
public sealed class ChatServiceTests
{
    private const string ProfileId = "chat-profile";
    private const string AgentConfigId = "chat-agent-config";
    private const string AgentId = "chat-agent-1";
    private const string ConnectionId = "conn-chat-agent-1";

    private readonly Mock<IAgentProfileStore> _profiles = new();
    private readonly Mock<IProviderConfigStore> _providerConfigs = new();
    private readonly Mock<IProjectStore> _projects = new();
    private readonly Mock<IChatJobDispatcher> _dispatcher = new();
    private readonly Mock<IAgentRegistryService> _registry = new();
    private readonly Mock<IAgentHubClient> _agent = new();
    private readonly ChatSessionStore _sessions = new(null, TimeSpan.FromHours(1));
    private readonly List<ChatPromptMessage> _delivered = [];
    private readonly ChatService _sut;

    public ChatServiceTests()
    {
        _profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Profile(ProfileId, "kiro,dotnet")]);
        _dispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentId);
        UseAgentConfig("KiroCli");
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
            _profiles.Object, _providerConfigs.Object, _projects.Object,
            JobTemplateStore.LoadFromYaml(Template("kiro,dotnet") + Template("claude,dotnet") + Template("")),
            _dispatcher.Object, _sessions, _registry.Object, hub.Object,
            new DispatchServiceOptions { ChatPodConnectTimeoutSeconds = 90 },
            NullLogger<ChatService>.Instance);
    }

    private static string Template(string labels) => $"- labels: \"{labels}\"\n  image: \"test/agent:latest\"\n  providerType: \"kiro\"\n";

    private static AgentProfile Profile(string id, string labels, bool enabled = true) => new()
    {
        Id = id,
        DisplayName = $"{id} profile",
        MatchLabels = labels.Split(',', StringSplitOptions.RemoveEmptyEntries),
        AgentProviderConfigId = AgentConfigId,
        Enabled = enabled,
    };

    private void UseAgentConfig(string providerType, Dictionary<string, string>? settings = null) =>
        _providerConfigs.Setup(p => p.GetProviderConfigByIdAsync(AgentConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig
            {
                Id = AgentConfigId, DisplayName = "Agent", Kind = ProviderKind.Agent, ProviderType = providerType,
                Settings = settings ?? [],
            });

    private static ChatSessionRecord Session(string? projectId = null, bool prompted = false) =>
        new(AgentId, "oidc", "oidc:tester", projectId, ProfileId, "window-1", prompted);

    // ── Options ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Options_OfferOnlyEnabledProfilesWhoseLabelsNameAJobTemplate()
    {
        // The empty-labels template exists so the label-less profile is excluded for having no
        // labels, not for lacking a template: a chat pod needs a non-empty selector.
        _profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Profile("kiro", "dotnet,kiro"),
            Profile("claude-disabled", "claude,dotnet", enabled: false),
            Profile("no-template", "java"),
            Profile("default", ""),
        ]);

        var options = await _sut.GetOptionsAsync(CancellationToken.None);

        options.Profiles.Select(p => p.Id).Should().Equal("kiro");
        options.ConnectTimeoutSeconds.Should().Be(90);
    }

    // ── Launch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_DispatchesTheProfilesSelector_WithItsProvidersModelAndEffort_AndRecordsTheOwner()
    {
        // Regression: the chat header showed "model: auto" and the pod ran the CLI default model
        // because the page looked the profile up from template labels and found none.
        UseAgentConfig("ClaudeCode", new() { [ProviderSettingKeys.Model] = "claude-opus-5-5", [ProviderSettingKeys.Effort] = "high" });
        var project = new PipelineProject { Id = "6f1c2a9e-0000-0000-0000-0000000000f1", Name = "Foxtrot" };

        var session = await _sut.StartAsync(ProfileId, project, "oidc", "oidc:tester", CancellationToken.None);

        session.Should().Be(new UiChatSession(AgentId, "claude-opus-5-5"));
        _dispatcher.Verify(d => d.DispatchChatPodAsync("dotnet,kiro", "claude-opus-5-5", "high", It.IsAny<CancellationToken>()), Times.Once);
        (await _sessions.FindAsync(AgentId)).Should().BeEquivalentTo(
            new { AgentId, OwnerIdentitySource = "oidc", OwnerSubject = "oidc:tester", ProjectId = project.Id, ProfileId, Prompted = false });
    }

    [Fact]
    public async Task Start_WithoutAModel_ReportsAuto()
    {
        (await _sut.StartAsync(ProfileId, null, "oidc", "oidc:tester", CancellationToken.None)).Model.Should().Be("auto");
    }

    [Fact]
    public async Task Start_AProfileThatCannotBeLaunched_IsRefusedWithoutDispatching()
    {
        var act = () => _sut.StartAsync("gone", null, "oidc", "oidc:tester", CancellationToken.None);

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.Should().BeEquivalentTo(new { StatusCode = StatusCodes.Status409Conflict, Message = "The selected agent profile is no longer available. Reload the page." });
        _dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Start_AProfileWithoutItsProviderConfig_IsRefusedWithoutDispatching()
    {
        // Launching anyway would run the pod with default model, effort and auth mode.
        _providerConfigs.Setup(p => p.GetProviderConfigByIdAsync(AgentConfigId, ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => _sut.StartAsync(ProfileId, null, "oidc", "oidc:tester", CancellationToken.None);

        (await act.Should().ThrowAsync<ChatRequestException>())
            .WithMessage("Agent profile 'chat-profile profile' points to an agent provider config that does not exist*");
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

        var act = () => _sut.StartAsync(ProfileId, null, "oidc", "oidc:tester", CancellationToken.None);

        (await act.Should().ThrowAsync<ChatRequestException>())
            .Which.Should().BeEquivalentTo(new { StatusCode = StatusCodes.Status502BadGateway, Message = reason });
        (await _sessions.FindAsync(AgentId)).Should().BeNull();
    }

    // ── Prompts ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OpenCode", null, "/home/ubuntu/.opencode/opencode.json")]
    [InlineData("KiroCli", null, "/home/ubuntu/.kiro/settings/mcp.json")]
    [InlineData("ClaudeCode", null, AgentDefaults.ClaudeMcpConfigPath)]
    [InlineData("OpenCode", "/custom/mcp.json", "/custom/mcp.json")]
    [InlineData("SomeNewProvider", null, "/home/ubuntu/.kiro/settings/mcp.json")]
    [InlineData("", null, "/home/ubuntu/.kiro/settings/mcp.json")]
    public async Task Prompt_CarriesTheMcpConfigPath_OfTheProfilesProvider(string providerType, string? explicitPath, string expected)
    {
        UseAgentConfig(providerType, explicitPath is null ? null : new() { [ProviderSettingKeys.McpConfigPath] = explicitPath });

        await _sut.SendPromptAsync(Session(), new UiChatPromptRequest("s1", "hello"), CancellationToken.None);

        _delivered.Single().McpConfigPath.Should().Be(expected);
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
