using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Every setting has a field on a settings page, and every setting a project can override has a field on the project's
/// Settings tab. Fields mark the setting they edit with <c>data-setting</c>; a setting added without a field, or a field
/// left behind for a removed setting, fails here.
/// </summary>
public class SettingsCoverageTests : BunitContext
{
    /// <summary>Not operator settings, so no page offers them (see <see cref="PipelineConfiguration"/>).</summary>
    private static readonly string[] InternalSettings =
    [
        nameof(PipelineConfiguration.ClosedLoopAutoStart),
        nameof(PipelineConfiguration.PipelineInjectedPaths),
        nameof(PipelineConfiguration.TransientRetryDelay),
        nameof(PipelineConfiguration.WorkspaceBaseDirectory),
    ];

    private readonly Mock<IPipelineApiConfigClient> _configClient = new();

    public SettingsCoverageTests()
    {
        _configClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
    }

    [Fact]
    public void EverySetting_HasAFieldOnASettingsPage()
    {
        var offered = new List<string>();
        offered.AddRange(SettingsOn(Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineDecompositionSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineCiSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineCodeReviewSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineConsolidationSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineTriageSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOn(Render<PipelineAdvancedSection>(p => p.Add(s => s.ConfigClient, _configClient.Object))));
        offered.AddRange(SettingsOnAgentProviderForm());

        var settings = PipelineSettingsValidator.SettingPaths();
        settings.Except(InternalSettings).Except(offered).Should().BeEmpty(
            "every setting needs a field on a settings page (or, if it is not an operator setting, an entry in InternalSettings)");
        offered.Except(settings).Should().BeEmpty("a field must edit an existing setting");
        offered.Should().OnlyHaveUniqueItems("each setting is edited in one place");
    }

    [Fact]
    public void EveryProjectOverridableSetting_HasAFieldOnTheProjectSettingsTab()
    {
        var project = new PipelineProject { Id = "p-1", Name = "Product" };
        _configClient.Setup(c => c.GetProjectByIdAsync("p-1", It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _configClient.Setup(c => c.GetProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProviderConfig>());
        _configClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineJobTemplate>());
        _configClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<PipelineProject> { project });

        var cut = Render<ProjectDetailSection>(p => p
            .Add(s => s.ProjectId, "p-1")
            .Add(s => s.ConfigClient, _configClient.Object));
        cut.FindAll(".tab-btn").Single(b => b.TextContent.Trim() == "Settings").Click();

        var offered = SettingsOn(cut);

        offered.Should().BeEquivalentTo(PipelineSettingsValidator.ProjectOverridablePaths(),
            "the project Settings tab offers exactly the settings a project can override");
    }

    /// <summary>The settings a rendered page offers, with every "Advanced settings" block expanded.</summary>
    private static List<string> SettingsOn<TComponent>(IRenderedComponent<TComponent> cut) where TComponent : IComponent
    {
        IElement? collapsed;
        while ((collapsed = cut.FindAll(".advanced-toggle").FirstOrDefault(t => t.QuerySelector(".toggle-chevron.expanded") is null)) is not null)
            collapsed.Click();

        return cut.FindAll("[data-setting]").Select(e => e.GetAttribute("data-setting")!).ToList();
    }

    /// <summary>ModelFetchTimeoutSeconds is edited in the Kiro agent provider form, in Kubernetes mode.</summary>
    private List<string> SettingsOnAgentProviderForm()
    {
        var cut = Render<AgentProviderSection>(p => p
            .Add(s => s.Providers, new List<ProviderConfig>())
            .Add(s => s.ConfigClient, _configClient.Object)
            .Add(s => s.IsKubernetesMode, true));
        cut.Find(".btn-add").Click();

        return SettingsOn(cut);
    }
}
