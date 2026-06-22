using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineQualityGatesSection.
/// </summary>
public class PipelineQualityGatesSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineQualityGatesSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void RendersHeader()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Implementation", cut.Markup);
    }

    [Fact]
    public void RendersReviewFields_WhenAdvancedExpanded()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        // Expand advanced toggle to reveal review fields
        var advancedToggle = cut.Find(".advanced-toggle");
        advancedToggle.Click();

        Assert.Contains("Max Review Iterations", cut.Markup);
        Assert.Contains("Fix Prompt", cut.Markup);
    }

    [Fact]
    public void DoesNotRenderExternalCiCheckbox()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.DoesNotContain("External CI Quality Gate", cut.Markup);
    }

    [Fact]
    public void RendersCiSettingsWithInfoHint()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        // CI settings are behind the advanced toggle
        Assert.Contains("Advanced settings", cut.Markup);
    }

    [Fact]
    public void RendersResetButtons_ForPrompts()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        // Expand advanced toggle to reveal code review section with reset buttons
        var advancedToggle = cut.Find(".advanced-toggle");
        advancedToggle.Click();

        var resetButtons = cut.FindAll(".btn-revert");
        Assert.True(resetButtons.Count >= 1);
    }

    [Fact]
    public void RendersReviewerConfigHint()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        // Expand advanced toggle to reveal reviewer config hint
        var advancedToggle = cut.Find(".advanced-toggle");
        advancedToggle.Click();

        Assert.Contains("Reviewer Configs", cut.Markup);
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Implementation"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Implementation"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }
}
