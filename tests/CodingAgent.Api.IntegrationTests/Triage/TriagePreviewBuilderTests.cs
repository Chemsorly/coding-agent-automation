using AwesomeAssertions;
using CodingAgent.Api.Triage;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// <see cref="TriagePreviewBuilder"/> (moved from the web host in spec 051 2.7): what an operator triage of a
/// project will use, served by <c>GET /api/ui/triages/preview</c>.
/// </summary>
public class TriagePreviewBuilderTests
{
    private const string ShopProjectId = "6f1c2a9e-0000-0000-0000-00000000aa01";

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

}
