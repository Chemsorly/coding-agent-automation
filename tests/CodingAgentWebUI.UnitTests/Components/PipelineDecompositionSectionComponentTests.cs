using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineDecompositionSection.
/// </summary>
public class PipelineDecompositionSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineDecompositionSectionComponentTests()
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
        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Decomposition", cut.Markup);
    }

    [Fact]
    public void RendersAllFields()
    {
        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Max Sub-Issues Per Epic", cut.Markup);
        Assert.Contains("Max Concurrent Decompositions", cut.Markup);
        Assert.Contains("Decomposition Timeout", cut.Markup);
        Assert.Contains("Max Open Issues for Context", cut.Markup);
    }

    [Fact]
    public void RendersHintIcons()
    {
        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        var hints = cut.FindAll(".form-hint-icon");
        Assert.Equal(4, hints.Count);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                MaxDecompositionSubIssues = 8,
                MaxConcurrentDecompositions = 4,
                DecompositionTimeout = TimeSpan.FromMinutes(30),
                MaxOpenIssuesForContext = 100
            });

        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "8");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "4");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "30");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "100");
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Decomposition"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Decomposition"));
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

        var cut = Render<PipelineDecompositionSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Decomposition"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Equal(10, saved!.MaxDecompositionSubIssues);
        Assert.Equal(2, saved.MaxConcurrentDecompositions);
        Assert.Equal(TimeSpan.FromMinutes(15), saved.DecompositionTimeout);
        Assert.Equal(50, saved.MaxOpenIssuesForContext);
    }
}
