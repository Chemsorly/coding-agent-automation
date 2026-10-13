using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.Chat;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiChatCollection : ICollectionFixture<ChatApiFactory>
{
    public const string Name = "UiChat";
}

/// <summary>The API with a mocked chat pod dispatcher and one chat job template, so a launch needs no cluster.</summary>
public sealed class ChatApiFactory : ApiWebApplicationFactory
{
    public const string TemplateLabels = "chat-endpoint-test";

    public Mock<IChatJobDispatcher> Dispatcher { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IChatJobDispatcher>();
        services.AddSingleton(Dispatcher.Object);
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(JobTemplateStore.LoadFromYaml(
            $"- labels: \"{TemplateLabels}\"\n  image: \"test/agent:latest\"\n  providerType: \"kiro\"\n"));
    }
}

/// <summary>
/// Spec 051 2.5b: Agent Chat on the UI surface (D5, closing F14). Launching needs operator on the chosen
/// project, or global operator without one. A chat can then only be used by the user who launched it, or
/// by an admin; anyone else, and any agent not launched here, gets 404. The web's real
/// <see cref="UiChatClient"/> drives the round trips, so this doubles as its contract test.
/// ChatServiceTests cover what a launch dispatches and what a prompt carries.
/// </summary>
[Collection(UiChatCollection.Name)]
public sealed class UiChatEndpointTests
{
    private const string ProjectA = "6f1c2a9e-0000-0000-0000-0000000000c7";
    private const string ProjectB = "6f1c2a9e-0000-0000-0000-0000000000c8";
    private const string ProfileId = "9c000000-0000-0000-0000-0000000000c7";
    private const string AgentConfigId = "8b000000-0000-0000-0000-0000000000c7";

    private readonly ChatApiFactory _factory;

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    public UiChatEndpointTests(ChatApiFactory factory)
    {
        _factory = factory;
        factory.Dispatcher.Reset();
    }

    private UiChatClient Client(AccessGrant grant, string subject = "oidc:tester") =>
        new(_factory.CreateClient(), new FixedToken(TestUserTokens.For(grant, subject)));

    private HttpClient Http(AccessGrant grant, string subject = "oidc:tester")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant, subject));
        return client;
    }

    private ChatSessionStore Sessions => _factory.Services.GetRequiredService<ChatSessionStore>();

    /// <summary>A chat of <paramref name="ownerSubject"/> in <paramref name="projectId"/>, as a launch records it.</summary>
    private async Task<string> SeedSessionAsync(string ownerSubject = "oidc:tester", string? projectId = null)
    {
        var agentId = $"chat-endpoint-{Guid.NewGuid():N}";
        await Sessions.SaveAsync(new ChatSessionRecord(agentId, "oidc", ownerSubject, projectId, ProfileId, "window"));
        return agentId;
    }

    private async Task SeedProfileAsync()
    {
        await _factory.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(new AgentProfile
        {
            Id = ProfileId, DisplayName = "Chat agent", AgentProviderConfigId = AgentConfigId, MatchLabels = [ChatApiFactory.TemplateLabels], Enabled = true,
        }, CancellationToken.None);
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
        {
            Id = AgentConfigId, Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Chat agent config",
            Settings = new() { [ProviderSettingKeys.Model] = "claude-opus-5-5" },
        }, CancellationToken.None);
    }

    // ── Launch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Launch_NeedsOperatorOnTheProject_OrGlobalOperatorWithoutOne()
    {
        var body = new UiChatStartRequest(ProfileId, ProjectB);

        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).PostAsJsonAsync("/api/ui/chat", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).PostAsJsonAsync("/api/ui/chat", body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Http(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).PostAsJsonAsync("/api/ui/chat", body with { ProjectId = null }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Launch_ThroughTheClient_StartsThePod_AndRecordsTheCaller()
    {
        await SeedProfileAsync();
        await _factory.Services.GetRequiredService<IProjectStore>().SaveProjectAsync(new PipelineProject { Id = ProjectA, Name = "Alpha" }, CancellationToken.None);
        _factory.Dispatcher.Setup(d => d.DispatchChatPodAsync(ChatApiFactory.TemplateLabels, "claude-opus-5-5", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("chat-endpoint-launched");
        var client = Client(TestUserTokens.Scoped(ProjectA, AccessRole.Operator));

        var options = await client.GetOptionsAsync();
        var session = await client.StartAsync(ProfileId, ProjectA);

        options.Profiles.Should().ContainEquivalentOf(new UiChatProfile(ProfileId, "Chat agent", [ChatApiFactory.TemplateLabels]));
        session.Should().Be(new UiChatSession("chat-endpoint-launched", "claude-opus-5-5"));
        (await Sessions.FindAsync("chat-endpoint-launched")).Should().BeEquivalentTo(
            new { OwnerIdentitySource = "oidc", OwnerSubject = "oidc:tester", ProjectId = ProjectA, ProfileId });
    }

    [Fact]
    public async Task Launch_ThatFails_ThrowsTheReasonThroughTheClient()
    {
        await SeedProfileAsync();
        _factory.Dispatcher.Setup(d => d.DispatchChatPodAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ChatPodTimeoutException(120));

        var act = () => Client(TestUserTokens.Global(AccessRole.Operator)).StartAsync(ProfileId, null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Pod did not connect within 120s. Check cluster logs.");
    }

    // ── Using a chat ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheOwner_KeepsTheChatAlive_AndEndsIt()
    {
        var agentId = await SeedSessionAsync();
        var owner = Client(TestUserTokens.Global(AccessRole.Operator));

        await owner.KeepaliveAsync(agentId);
        await owner.TerminateAsync(agentId);

        _factory.Dispatcher.Verify(d => d.SendClientKeepalive(agentId), Times.Once);
        _factory.Dispatcher.Verify(d => d.TerminateChatSessionAsync(new AgentId(agentId), It.IsAny<CancellationToken>()), Times.Once);
        (await Sessions.FindAsync(agentId)).Should().BeNull();
    }

    [Fact]
    public async Task AnotherUser_CannotUseTheChat_ButAnAdminCan()
    {
        var agentId = await SeedSessionAsync();
        var other = Http(TestUserTokens.Global(AccessRole.Operator), subject: "oidc:someone-else");

        (await other.PostAsync($"/api/ui/chat/{agentId}/keepalive", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/ui/chat/{agentId}/terminate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/ui/chat/{agentId}/prompt", new UiChatPromptRequest("s", "hi"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Dispatcher.VerifyNoOtherCalls();

        await Client(TestUserTokens.Global(AccessRole.Admin), subject: "oidc:admin").KeepaliveAsync(agentId);
        _factory.Dispatcher.Verify(d => d.SendClientKeepalive(agentId), Times.Once);
    }

    [Fact]
    public async Task TheOwner_WhoLostOperatorOnTheProject_CannotUseTheChat()
    {
        var agentId = await SeedSessionAsync(projectId: ProjectA);

        (await Http(TestUserTokens.Scoped(ProjectA, AccessRole.ReadOnly)).PostAsync($"/api/ui/chat/{agentId}/keepalive", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a read-only user is refused by the operator policy first");
        (await Http(TestUserTokens.Scoped(ProjectB, AccessRole.Operator)).PostAsync($"/api/ui/chat/{agentId}/keepalive", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Dispatcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnAgentNotLaunchedHere_CannotBePrompted()
    {
        var registry = _factory.Services.GetRequiredService<AgentRegistryService>();
        var pipelineAgent = $"pipeline-agent-{Guid.NewGuid():N}";
        registry.Register(new AgentRegistrationMessage { AgentId = new AgentId(pipelineAgent), Hostname = "pod", Labels = ["dotnet"] }, "conn-pipeline");

        var act = () => Client(TestUserTokens.Global(AccessRole.Admin)).SendPromptAsync(pipelineAgent, "s", "hi");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Prompt_ToAPodThatIsGone_ThrowsTheReasonThroughTheClient()
    {
        var agentId = await SeedSessionAsync();
        await SeedProfileAsync();

        var act = () => Client(TestUserTokens.Global(AccessRole.Operator)).SendPromptAsync(agentId, "s", "hi");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("The chat pod is gone*");
    }
}
