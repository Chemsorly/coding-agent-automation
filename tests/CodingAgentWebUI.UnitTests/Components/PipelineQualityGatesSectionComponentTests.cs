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
        Assert.Contains("Quality Gates", cut.Markup);
    }

    [Fact]
    public void RendersReviewFields_Always()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
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

        Assert.Contains("External CI Settings", cut.Markup);
        Assert.Contains("Pipeline Provider is configured", cut.Markup);
        Assert.Contains("CI Timeout", cut.Markup);
        Assert.Contains("CI Poll Interval", cut.Markup);
    }

    [Fact]
    public void RendersResetButtons_ForPrompts()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var resetButtons = cut.FindAll(".btn-revert");
        Assert.True(resetButtons.Count >= 1);
    }

    [Fact]
    public void RendersReviewerConfigHint()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Reviewer Configs", cut.Markup);
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Quality Gate"));
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

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Quality Gate"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }
}
