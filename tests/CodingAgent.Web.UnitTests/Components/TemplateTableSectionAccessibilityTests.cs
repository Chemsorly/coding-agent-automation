using Bunit;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;
using CodingAgent.Orchestration.Registry;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests that TemplateTableSection form controls have accessible names.
/// </summary>
public class TemplateTableSectionAccessibilityTests : BunitContext
{
    private static readonly PipelineJobTemplate TestTemplate = new()
    {
        Id = "t-1",
        Name = "My Template",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true,
        ImplementationEnabled = true,
        ReviewEnabled = true,
        DecompositionEnabled = true,
        HousekeepingEnabled = false,
        HousekeepingBranchCleanupEnabled = false,
    };

    public TemplateTableSectionAccessibilityTests()
    {
        var registry = new AgentRegistryService(Log.Logger);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
    }

    private IRenderedComponent<TemplateTableSection> RenderMinimal(PipelineJobTemplate? template = null)
    {
        var templates = new[] { template ?? TestTemplate };
        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "Default",
            TemplateIds = templates.Select(t => t.Id).ToList(),
        };

        return Render<TemplateTableSection>(p => p
            .Add(s => s.Templates, templates)
            .Add(s => s.Projects, new[] { project })
            .Add(s => s.IssueProviders, new List<ProviderConfig>())
            .Add(s => s.RepoProviders, new List<ProviderConfig>())
            .Add(s => s.BrainProviders, new List<ProviderConfig>())
            .Add(s => s.PipelineProviders, new List<ProviderConfig>())
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.RecentlyToggled, new HashSet<string>())
            .Add(s => s.TemplateStatuses, new Dictionary<string, ConfigStatusSnapshot>())
            .Add(s => s.QualityGateConfigs, Array.Empty<QualityGateConfiguration>())
            .Add(s => s.ReviewerConfigs, Array.Empty<ReviewerConfiguration>())
            .Add(s => s.AgentProfiles, Array.Empty<AgentProfile>())
            .Add(s => s.PipelineConfig, new PipelineConfiguration()));
    }

    [Fact]
    public void TemplateTableSection_EnabledCheckbox_HasAriaLabel()
    {
        var cut = RenderMinimal();

        // Find the Enabled toggle checkbox and verify it has an aria-label containing the template name
        var checkboxes = cut.FindAll("input[type='checkbox']");
        // TODO [WARNING]: The enabled toggle is located by position (FirstOrDefault) rather than a stable
        // selector. If the table renders any checkbox before the enabled toggle (e.g. a select-all or
        // row-selection control), this assertion silently validates the wrong element. Use a
        // data-testid or locate by aria-label prefix instead.
        var enabledCheckbox = checkboxes.FirstOrDefault();
        Assert.NotNull(enabledCheckbox);

        var ariaLabel = enabledCheckbox!.GetAttribute("aria-label");
        Assert.NotNull(ariaLabel);
        Assert.Contains("My Template", ariaLabel);
    }

    [Fact]
    public async Task TemplateTableSection_FeatureExpansion_CheckboxesHaveAriaLabels()
    {
        var cut = RenderMinimal();

        // Expand the feature panel by clicking the feature badge bar button
        var expandBtn = cut.Find("button.feature-badge-bar");
        Assert.NotNull(expandBtn);
        await cut.InvokeAsync(() => expandBtn.Click());

        // After expansion, feature checkboxes should be visible with aria-labels
        var checkboxes = cut.FindAll("input[type='checkbox']");
        // TODO [WARNING]: Assert >= 6 does not catch the case where the expansion click is a no-op
        // (wrong selector) and 0 checkboxes are present — a foreach over 0 items trivially passes.
        // Consider asserting the count increased relative to the pre-expansion count, or assert == 6.
        Assert.True(checkboxes.Count >= 6, $"Expected at least 6 checkboxes, found {checkboxes.Count}");

        // All checkboxes must have aria-label containing the template name
        foreach (var checkbox in checkboxes)
        {
            var ariaLabel = checkbox.GetAttribute("aria-label");
            Assert.NotNull(ariaLabel);
            Assert.Contains("My Template", ariaLabel);
        }
    }

    [Fact]
    public void TemplateTableSection_EnabledCheckbox_AriaLabel_ContainsEnableVerb()
    {
        var cut = RenderMinimal();

        var checkboxes = cut.FindAll("input[type='checkbox']");
        var enabledCheckbox = checkboxes.FirstOrDefault();
        Assert.NotNull(enabledCheckbox);

        var ariaLabel = enabledCheckbox!.GetAttribute("aria-label");
        Assert.NotNull(ariaLabel);
        // Label should say "Enable [template name]"
        Assert.StartsWith("Enable", ariaLabel, StringComparison.OrdinalIgnoreCase);
    }
}
