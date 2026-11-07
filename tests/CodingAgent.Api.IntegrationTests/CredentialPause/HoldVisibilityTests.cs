using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Api.IntegrationTests.AgentCapacity;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Api.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using k8s.Models;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// Spec 056 DD7, req 4.1, 4.3, 4.6 (tasks 4.6, 4.7): derived hold texts for items the loop never sent, and the pause
/// text on routing rows and capacity rows.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class HoldVisibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());
    private readonly SettableAgentJobs _jobs = new();

    private static readonly JobTemplateStore Templates = JobTemplateStore.LoadFromYaml("""
        - labels: "kiro,dotnet"
          image: "agent:test"
          providerType: "kiro"
          maxConcurrent: 1
        - labels: "claude"
          image: "agent:test"
          providerType: "claude"
        """);

    private static readonly AgentProfile Kiro = AgentRoutingTestData.Profile("p-kiro", "kiro,dotnet", "Kiro");
    private static readonly AgentProfile ClaudeKey = AgentRoutingTestData.Profile(
        "p-claude", "claude", "Claude key", settings: new Dictionary<string, string> { ["authMode"] = "apiKey" });

    private CredentialPauseStore Pauses() =>
        new(_db, new AgentRoutingTestSupport.SemaphoreLockProvider(), new SettableTime(Now), Serilog.Core.Logger.None);

    private AgentCapacityReader Reader(int helmGlobal = 0)
    {
        var profiles = new Mock<IAgentProfileStore>();
        profiles.Setup(p => p.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Kiro, ClaudeKey]);
        return new AgentCapacityReader(
            _jobs, Templates, new DispatchServiceOptions { Namespace = "test", MaxConcurrentAgents = helmGlobal },
            new AgentCapacityStore(_db), Serilog.Core.Logger.None, profiles.Object, Pauses());
    }

    private static V1Job RunJob(string label) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Labels = new Dictionary<string, string> { ["caa/agent-selector"] = label, ["caa/work-item-id"] = Guid.NewGuid().ToString() },
        },
        Status = new V1JobStatus(),
    };

    private static PendingWorkItemDto Item(string selector, string? credential, string? holdReason = null, string? holdText = null) => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = "1",
        IssueProviderConfigId = "ip",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = Now,
        AgentSelector = selector,
        RetryCount = 0,
        TimeoutSeconds = 3600,
        Credential = credential,
        HoldReason = holdReason,
        HoldText = holdText,
    };

    private async Task PauseAsync(string credential) =>
        await Pauses().PauseAsync(new AgentCredentialPause(
            credential, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, null, "limit"), CancellationToken.None);

    // DD7: a paused credential first, then the global limit, then the template limit; a stored hold wins.
    [Fact]
    public async Task Derive_PausedCredential_ThenTemplateLimit_StoredHoldWins()
    {
        await PauseAsync(AgentCredentials.ClaudeApiKey);
        _jobs.Jobs.Add(RunJob("dotnet.kiro"));
        var items = new List<PendingWorkItemDto>
        {
            Item("claude", AgentCredentials.ClaudeApiKey),
            Item("dotnet,kiro", AgentCredentials.Kiro),
            Item("dotnet,kiro", AgentCredentials.Kiro, HoldReasons.NoRoute, "stored text"),
            Item("", null),
        };

        var derived = await PendingHoldTexts.DeriveAsync(items, new PendingHoldSources(Reader(), Pauses()), CancellationToken.None);

        derived[0].HoldText.Should().Be("Claude API key paused — quota used up, since 2026-11-02 06:26 UTC");
        derived[1].HoldText.Should().Be("1 of 1 agents are running for dotnet,kiro (limit set in the Helm chart)");
        derived[2].HoldText.Should().Be("stored text");
        derived[3].HoldText.Should().BeNull("an item never resolved has nothing to derive from");
        derived[1].HoldReason.Should().BeNull("a derived text is no stored hold");
    }

    [Fact]
    public async Task Derive_GlobalLimit_AppliesToEveryUnheldItem()
    {
        _jobs.Jobs.Add(RunJob("claude"));

        var derived = await PendingHoldTexts.DeriveAsync(
            [Item("claude", AgentCredentials.ClaudeApiKey)], new PendingHoldSources(Reader(helmGlobal: 1), Pauses()), CancellationToken.None);

        derived[0].HoldText.Should().Be("1 of 1 agents are running (limit set in the Helm chart)");
    }

    // req 4.6: a Capacity row shows the pause of the credentials its profiles use.
    [Fact]
    public async Task CapacityRows_CarryThePauseTextOfTheirCredential()
    {
        await PauseAsync(AgentCredentials.Kiro);

        var page = await Reader().ReadPageAsync(CancellationToken.None);

        page.Templates.Single(r => r.TemplateKey == "dotnet,kiro").CredentialPausedText.Should().StartWith("Kiro paused");
        page.Templates.Single(r => r.TemplateKey == "claude").CredentialPausedText.Should().BeNull();
    }

    // req 4.3: a routing row whose credential is paused says so.
    [Fact]
    public void ResolvedRoutes_MarkARowWhoseCredentialIsPaused()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "t", IssueProviderId = "i", RepoProviderId = "r" };
        var pause = new AgentCredentialPause(
            AgentCredentials.ClaudeApiKey, CredentialPauseReasons.Manual, CredentialPauseSources.Manual, Now, null, "", PausedBy: "alice");

        var routes = ResolvedRoutes.For(
            [template], AgentRoutingTestData.Routing("p-claude"), [Kiro, ClaudeKey], Templates.ToolsByKey(), [pause]);

        routes["t1"].Should().OnlyContain(r => r.CredentialPausedText == "Claude API key paused — paused by alice, since 2026-11-02 06:26 UTC");
        ResolvedRoutes.For([template], AgentRoutingTestData.Routing("p-kiro"), [Kiro, ClaudeKey], Templates.ToolsByKey(), [pause])["t1"]
            .Should().OnlyContain(r => r.CredentialPausedText == null);
    }
}
