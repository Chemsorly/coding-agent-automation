using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Structural tests for TemplateTableSection toggle markup.
///
/// These tests verify that the HTML structure expected by the toggle-switch CSS rules
/// is present in the rendered output. bUnit does not apply CSS stylesheets, so they
/// cannot detect CSS scoping issues directly, but they guard the markup contract:
/// if the classes disappear, the switch will always render as a native checkbox
/// regardless of whether a .razor.css file exists.
///
/// See issue #2938: Blazor scoped CSS in AgentCoding.razor.css does not cross
/// component boundaries into TemplateTableSection — requiring its own .razor.css.
/// </summary>
public class TemplateTableSectionComponentTests : BunitContext
{
    private static readonly PipelineJobTemplate TestTemplate = new()
    {
        Id = "t-1",
        Name = "Test Template",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true,
        ImplementationEnabled = true,
        ReviewEnabled = true,
        DecompositionEnabled = false,
        HousekeepingEnabled = false,
        HousekeepingBranchCleanupEnabled = false
    };

    private static readonly PipelineProject TestProject = new()
    {
        Id = "proj-1",
        Name = "Test Project",
        Enabled = true,
        TemplateIds = new[] { "t-1" }
    };

    public TemplateTableSectionComponentTests()
    {
        // TemplateTableSection injects IAgentRegistryService via [Inject]
        var mockRegistry = new Mock<IAgentRegistryService>();
        mockRegistry.Setup(r => r.GetAllAgents()).Returns(System.Array.Empty<AgentEntry>());
        Services.AddSingleton(mockRegistry.Object);
        Services.AddSingleton<IAgentRegistryService>(mockRegistry.Object);
    }

    private IRenderedComponent<TemplateTableSection> RenderSection(
        PipelineJobTemplate? template = null,
        bool isLoopActive = false)
    {
        var t = template ?? TestTemplate;
        return Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, new[] { t })
            .Add(s => s.Projects, new[] { TestProject })
            .Add(s => s.IssueProviders, new System.Collections.Generic.List<ProviderConfig>())
            .Add(s => s.RepoProviders, new System.Collections.Generic.List<ProviderConfig>())
            .Add(s => s.BrainProviders, new System.Collections.Generic.List<ProviderConfig>())
            .Add(s => s.PipelineProviders, new System.Collections.Generic.List<ProviderConfig>())
            .Add(s => s.IsLoopActive, isLoopActive)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, System.Array.Empty<QualityGateConfiguration>())
            .Add(s => s.ReviewerConfigs, System.Array.Empty<ReviewerConfiguration>())
            .Add(s => s.AgentProfiles, System.Array.Empty<AgentProfile>())
            .Add(s => s.PipelineConfig, new PipelineConfiguration()));
    }

    /// <summary>
    /// The "Enabled" column toggle in the template table row must use the custom
    /// toggle-switch/toggle-slider markup so it renders as a switch control in the browser.
    /// Regression guard: if the markup changes back to a plain &lt;input type="checkbox"&gt;
    /// without the wrapper, this test fails.
    /// </summary>
    [Fact]
    public void EnabledToggle_RendersAsToggleSwitch()
    {
        var cut = RenderSection();

        // TODO: String substring match on the full HTML blob is weaker than a DOM query —
        // a class name in a comment, data attribute, or unrelated element would make this
        // assertion pass even if the actual toggle lost its class. EnabledToggle_HasCorrectDomStructure
        // already validates the DOM structure precisely; consider removing or replacing
        // these markup string checks with structural assertions. (review finding #2938)
        // The Enabled column must have a label with class toggle-switch
        Assert.Contains("toggle-switch", cut.Markup);
        // And the inner slider span
        Assert.Contains("toggle-slider", cut.Markup);
    }

    /// <summary>
    /// The "Enabled" toggle must be wrapped in a &lt;label class="toggle-switch"&gt; element
    /// containing an input and a toggle-slider span — not a raw checkbox.
    /// </summary>
    [Fact]
    public void EnabledToggle_HasCorrectDomStructure()
    {
        var cut = RenderSection();

        var toggleLabel = cut.Find("label.toggle-switch");
        Assert.NotNull(toggleLabel);

        // Must contain a hidden checkbox input
        var input = toggleLabel.QuerySelector("input[type='checkbox']");
        Assert.NotNull(input);

        // Must contain the slider span
        var slider = toggleLabel.QuerySelector("span.toggle-slider");
        Assert.NotNull(slider);
    }

    /// <summary>
    /// When the feature expansion panel is open, each of the five feature toggles
    /// (Implementation, PR Review, Decomposition, Housekeeping, Branch Cleanup) must
    /// also use toggle-switch markup — not raw checkboxes.
    /// </summary>
    [Fact]
    public async Task FeatureToggleExpansion_RendersToggleSwitches()
    {
        var cut = RenderSection();

        // Click the feature badge bar to expand the config panel
        var featureBtn = cut.Find("button.feature-badge-bar");
        await cut.InvokeAsync(() => featureBtn.Click());

        // After expansion, the feature config panel must appear with toggle-switch controls
        var featurePanel = cut.Find(".feature-config-panel");
        Assert.NotNull(featurePanel);

        var toggleLabels = featurePanel.QuerySelectorAll("label.toggle-switch");
        // There are 5 feature toggles: Implementation, PR Review, Decomposition, Housekeeping, Branch Cleanup
        Assert.Equal(5, toggleLabels.Length);

        foreach (var label in toggleLabels)
        {
            var slider = label.QuerySelector("span.toggle-slider");
            Assert.NotNull(slider);
        }
    }
}
