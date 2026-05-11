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
        Assert.Contains("Review Prompt", cut.Markup);
        Assert.Contains("Fix Prompt", cut.Markup);
    }

    [Fact]
    public void DoesNotRenderCodeReviewCheckbox()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.DoesNotContain("Agent Code Review Enabled", cut.Markup);
    }

    [Fact]
    public void ExternalCiCheckbox_DisabledWhenNoPipelineProviders()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.PipelineProviderCount, 0));

        var checkboxes = cut.FindAll("input[type='checkbox']");
        var ciCheckbox = checkboxes.First(); // External CI is the only checkbox
        Assert.True(ciCheckbox.HasAttribute("disabled"));
    }

    [Fact]
    public void ExternalCiCheckbox_EnabledWhenPipelineProvidersExist()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.PipelineProviderCount, 2));

        var checkboxes = cut.FindAll("input[type='checkbox']");
        var ciCheckbox = checkboxes.First();
        Assert.False(ciCheckbox.HasAttribute("disabled"));
    }

    [Fact]
    public void WhenExternalCiEnabled_ShowsCiFields()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { ExternalCiEnabled = true });

        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.PipelineProviderCount, 1));

        Assert.Contains("CI Timeout", cut.Markup);
        Assert.Contains("CI Poll Interval", cut.Markup);
    }

    [Fact]
    public void RendersResetButtons_ForPrompts()
    {
        var cut = Render<PipelineQualityGatesSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var resetButtons = cut.FindAll(".btn-revert");
        Assert.True(resetButtons.Count >= 2);
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
