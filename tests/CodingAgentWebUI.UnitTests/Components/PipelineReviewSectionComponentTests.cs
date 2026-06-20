using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineReviewSection.
/// </summary>
public class PipelineReviewSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineReviewSectionComponentTests()
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
        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Review", cut.Markup);
    }

    [Fact]
    public void RendersAllFields()
    {
        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Enable Inline Review Comments", cut.Markup);
        Assert.Contains("Minimum Severity", cut.Markup);
        Assert.Contains("Maximum Inline Comments", cut.Markup);
        Assert.Contains("Prioritize by Severity", cut.Markup);
        Assert.Contains("Format Correction Retries", cut.Markup);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    InlineComments = new InlineCommentSettings
                    {
                        Enabled = false,
                        SeverityThreshold = FindingSeverity.Critical,
                        MaxInlineComments = 25,
                        OrderBySeverity = false,
                        MaxRetries = 3
                    }
                }
            });

        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "25");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "3");
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Review"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Review"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public async Task Save_PersistsInlineCommentSettings()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineReviewSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Review"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.True(saved!.CodeReview.InlineComments.Enabled);
        Assert.Equal(FindingSeverity.Warning, saved.CodeReview.InlineComments.SeverityThreshold);
        Assert.Equal(15, saved.CodeReview.InlineComments.MaxInlineComments);
        Assert.True(saved.CodeReview.InlineComments.OrderBySeverity);
        Assert.Equal(1, saved.CodeReview.InlineComments.MaxRetries);
    }
}
