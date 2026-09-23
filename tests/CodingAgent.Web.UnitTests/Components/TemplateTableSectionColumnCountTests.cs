using AwesomeAssertions;
using Bunit;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for TemplateTableSection column count logic (issue #2939).
/// _columnCount must be 9 when loop is inactive (Actions column visible)
/// and 8 when loop is active (Actions column hidden), ensuring colspan values
/// on feature-config and label-preview rows stay in sync with the header.
/// </summary>
public class TemplateTableSectionColumnCountTests : BunitContext
{
    private static readonly string TemplateId = "t-col-1";
    private static readonly string IssueProviderId = "ip-col-1";
    private static readonly string RepoProviderId = "rp-col-1";

    private static PipelineJobTemplate DefaultTemplate => new()
    {
        Id = TemplateId,
        Name = "Test Template",
        IssueProviderId = IssueProviderId,
        RepoProviderId = RepoProviderId,
        ImplementationEnabled = true,
        ReviewEnabled = true,
        DecompositionEnabled = false,
        HousekeepingEnabled = false,
        Enabled = true,
    };

    private static PipelineProject DefaultProject => new()
    {
        Id = "proj-col-1",
        Name = "Test Project",
        TemplateIds = [TemplateId],
    };

    private static List<ProviderConfig> IssueProviders =>
    [
        new ProviderConfig { Id = IssueProviderId, DisplayName = "Issue Provider", Kind = ProviderKind.Issue, ProviderType = "GitHub" }
    ];

    private static List<ProviderConfig> RepoProviders =>
    [
        new ProviderConfig { Id = RepoProviderId, DisplayName = "Repo Provider", Kind = ProviderKind.Repository, ProviderType = "GitHub" }
    ];

    public TemplateTableSectionColumnCountTests()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton<IAgentRegistryService>(registry);
    }

    private IRenderedComponent<TemplateTableSection> RenderSection(bool isLoopActive) =>
        Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new List<PipelineJobTemplate> { DefaultTemplate })
            .Add(s => s.Projects, new List<PipelineProject> { DefaultProject })
            .Add(s => s.IssueProviders, IssueProviders)
            .Add(s => s.RepoProviders, RepoProviders)
            .Add(s => s.BrainProviders, [])
            .Add(s => s.PipelineProviders, [])
            .Add(s => s.IsLoopActive, isLoopActive)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, [])
            .Add(s => s.ReviewerConfigs, [])
            .Add(s => s.AgentProfiles, [])
            .Add(s => s.PipelineConfig, new PipelineConfiguration()));

    [Fact]
    public void WhenLoopInactive_ActionsColumnHeaderIsPresent()
    {
        var cut = RenderSection(isLoopActive: false);

        var headers = cut.FindAll("thead th")
            .Select(th => th.TextContent.Trim())
            .ToList();

        headers.Should().Contain("Actions",
            "the Actions column must appear in the header when the loop is inactive");
    }

    [Fact]
    public void WhenLoopActive_ActionsColumnHeaderIsAbsent()
    {
        var cut = RenderSection(isLoopActive: true);

        var headers = cut.FindAll("thead th")
            .Select(th => th.TextContent.Trim())
            .ToList();

        headers.Should().NotContain("Actions",
            "the Actions column must be hidden from the header when the loop is active");
    }

    [Fact]
    public async Task WhenLoopInactive_FeatureConfigRowColspanIsNine()
    {
        var cut = RenderSection(isLoopActive: false);

        // Expand the feature-config row by clicking the feature-badge-bar button.
        var expandButton = cut.Find("button.feature-badge-bar");
        await expandButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var featureRow = cut.Find("tr.feature-config-row");
        var td = featureRow.QuerySelector("td[colspan]");
        td.Should().NotBeNull("the feature-config row must contain a colspan td");
        td!.GetAttribute("colspan").Should().Be("9",
            "when the loop is inactive the 9-column layout (including Actions) must be reflected in the colspan");
    }

    [Fact]
    public async Task WhenLoopActive_FeatureConfigRowColspanIsEight()
    {
        var cut = RenderSection(isLoopActive: true);

        // Expand the feature-config row by clicking the feature-badge-bar button.
        var expandButton = cut.Find("button.feature-badge-bar");
        await expandButton.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var featureRow = cut.Find("tr.feature-config-row");
        var td = featureRow.QuerySelector("td[colspan]");
        td.Should().NotBeNull("the feature-config row must contain a colspan td");
        td!.GetAttribute("colspan").Should().Be("8",
            "when the loop is active the 8-column layout (Actions hidden) must be reflected in the colspan");
    }
}
