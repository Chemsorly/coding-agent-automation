using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineConsolidationSection.
/// </summary>
public class PipelineConsolidationSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineConsolidationSectionComponentTests()
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
        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Consolidation", cut.Markup);
    }

    [Fact]
    public void RendersAllFields()
    {
        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Max Refactoring Proposals", cut.Markup);
        Assert.Contains("Refactoring Review", cut.Markup);
        Assert.Contains("Brain Consolidation Review", cut.Markup);
        Assert.Contains("Harness Suggestions Review", cut.Markup);
        Assert.Contains("Advanced settings", cut.Markup);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                MaxRefactoringProposals = 5,
                HotspotAnalysisLookback = TimeSpan.FromDays(60),
                RefactoringOutcomeLookback = TimeSpan.FromDays(45),
                RefactoringReviewEnabled = false,
                BrainConsolidationReviewEnabled = false,
                HarnessSuggestionsReviewEnabled = false
            });

        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "5");
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Consolidation"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Consolidation"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public async Task Save_PersistsCorrectValues()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineConsolidationSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Consolidation"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Equal(3, saved!.MaxRefactoringProposals);
        Assert.Equal(TimeSpan.FromDays(90), saved.HotspotAnalysisLookback);
        Assert.Equal(TimeSpan.FromDays(90), saved.RefactoringOutcomeLookback);
        Assert.True(saved.RefactoringReviewEnabled);
        Assert.True(saved.BrainConsolidationReviewEnabled);
        Assert.True(saved.HarnessSuggestionsReviewEnabled);
    }
}
