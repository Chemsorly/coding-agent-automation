using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;
using static CodingAgent.Web.UnitTests.Components.TriageTestData;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary><see cref="TriageDisplay"/>, <see cref="TriagePreviewBuilder"/> and the triage group of <see cref="AttentionAggregator"/>.</summary>
public class TriageDisplayAndPreviewTests
{
    [Fact]
    public void EveryStatus_HasALabelAndATone()
    {
        foreach (var status in Enum.GetValues<TriageStatus>())
        {
            TriageDisplay.StatusLabel(status).Should().NotBe(status.ToString().ToLowerInvariant());
            TriageDisplay.StatusTone(status).Should().BeOneOf("run", "warn", "ok", "err", "mute");
        }
    }

    [Fact]
    public void ChecksBySource_GroupsByThePartBeforeTheDot_MostFirst()
    {
        var checks = new[]
        {
            new TriageCheck { Check = "a", Where = "grafana · Loki", Result = "r" },
            new TriageCheck { Check = "b", Where = "Grafana · Prometheus", Result = "r" },
            new TriageCheck { Check = "c", Where = "sonarqube", Result = "r" },
            new TriageCheck { Check = "d", Where = " ", Result = "r" },
        };

        TriageDisplay.ChecksBySource(checks).Should().Equal(("grafana", 2), ("sonarqube", 1));
    }

    [Theory]
    [InlineData("https://grafana.example/d/1", "https://grafana.example/d/1")]
    [InlineData("http://intranet/x", "http://intranet/x")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/relative", null)]
    [InlineData(null, null)]
    public void SafeLink_AllowsOnlyAbsoluteHttp(string? url, string? expected) =>
        TriageDisplay.SafeLink(url).Should().Be(expected);

    // ── Preview ──────────────────────────────────────────────────────────────

    private static readonly PipelineJobTemplate Web = new() { Id = "t-web", Name = "storefront-web", IssueProviderId = "ip-web", RepoProviderId = "rp-web", Enabled = true };
    private static readonly PipelineJobTemplate Api = new() { Id = "t-api", Name = "checkout-api", IssueProviderId = "ip-api", RepoProviderId = "rp-api", Enabled = true, BrainProviderId = "brain" };
    private static readonly PipelineJobTemplate Off = new() { Id = "t-off", Name = "admin", IssueProviderId = "ip-api", RepoProviderId = "rp-off", Enabled = false };

    private static readonly AgentProfile Profile = new()
    {
        Id = "p1", DisplayName = "default", AgentProviderConfigId = "ap-1", Enabled = true, MatchLabels = ["dotnet"],
        McpServers = [new McpServerConfig { Name = "grafana" }, new McpServerConfig { Name = "sonarqube" }, new McpServerConfig { Name = "old", Disabled = true }],
    };

    private static PipelineProject Project(string? epicTracker = "ip-epics") => new()
    {
        Id = ShopProjectId, Name = "Shop", Enabled = true, EpicIssueProviderId = epicTracker, SteeringContent = "Be careful",
        McpServers = [new McpServerConfig { Name = "grafana", Url = "https://grafana.internal" }, new McpServerConfig { Name = "confluence" }],
    };

    [Fact]
    public void Executor_IsTheFirstEnabledTemplateByName_UnlessStartInNamesAnEnabledOne()
    {
        TriagePreviewBuilder.SelectExecutor([Web, Api, Off], null)!.Id.Should().Be("t-api");
        TriagePreviewBuilder.SelectExecutor([Web, Api, Off], "t-web")!.Id.Should().Be("t-web");
        TriagePreviewBuilder.SelectExecutor([Web, Api, Off], "t-off")!.Id.Should().Be("t-api", "a disabled template cannot run the job");
        TriagePreviewBuilder.SelectExecutor([Off], null).Should().BeNull();
    }

    [Fact]
    public void Preview_MergesProfileAndProjectServers_ProjectWinsAndDisabledAreLeftOut()
    {
        var preview = TriagePreviewBuilder.Build(Project(), [Web, Api, Off], null, [], [Profile], new PipelineConfiguration { DefaultRequiredAgentLabels = "dotnet" });

        preview.Problem.Should().BeNull();
        preview.McpServers.Should().Equal(
            new TriagePreviewServer("confluence", "project"),
            new TriagePreviewServer("grafana", "project"),
            new TriagePreviewServer("sonarqube", "profile"));
        preview.Repositories.Select(r => (r.Name, r.IsExecutor)).Should().Equal(("checkout-api", true), ("storefront-web", false));
        preview.TrackerCount.Should().Be(3, "two template trackers and the epic tracker");
        preview.HasBrain.Should().BeTrue();
        preview.HasProjectSteering.Should().BeTrue();
    }

    [Fact]
    public void Preview_NoMatchingProfile_IsAProblem()
    {
        var preview = TriagePreviewBuilder.Build(Project(), [Web], null, [], [Profile], new PipelineConfiguration { DefaultRequiredAgentLabels = "python" });

        preview.Problem.Should().Contain("No agent profile matches");
    }

    [Fact]
    public void Preview_NoEnabledTemplate_IsAProblem()
    {
        var preview = TriagePreviewBuilder.Build(Project(), [Off], null, [], [Profile], new PipelineConfiguration());

        preview.Problem.Should().Contain("no enabled template");
        preview.Repositories.Should().BeEmpty();
    }

    // ── Attention ────────────────────────────────────────────────────────────

    [Fact]
    public void Attention_KeepsTriagesThatNeedSomeone_AndCountsThem()
    {
        var result = AttentionAggregator.Aggregate([], [
            ListItem(TriageStatus.NeedsReview), ListItem(TriageStatus.NeedsInput), ListItem(TriageStatus.Investigating), ListItem(TriageStatus.IssuesCreated),
        ]);

        result.TriagesNeedingYou.Select(t => t.Status).Should().Equal(TriageStatus.NeedsReview, TriageStatus.NeedsInput);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public void Attention_WithoutTriages_CountsAsBefore() =>
        AttentionAggregator.Aggregate([]).TotalCount.Should().Be(0);

    [Fact]
    public async Task Attention_TriageApiDown_DegradesToNoTriages()
    {
        var client = new Mock<IPipelineApiTriageClient>();
        client.Setup(c => c.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        (await AttentionAggregator.LoadTriagesNeedingYouAsync(client.Object, "", CancellationToken.None)).Should().BeEmpty();
        client.Verify(c => c.ListAsync(It.Is<TriageListQuery>(q => q.ProjectId == null && q.Tab == TriageListTab.NeedYou), It.IsAny<CancellationToken>()));
    }
}
