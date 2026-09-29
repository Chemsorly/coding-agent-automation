using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.CodeReview.Models;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineCodeReviewSection (Settings → Global Defaults → Code Review), which holds the review
/// step of implementation runs, the PR review settings and the acceptance criteria check.
/// </summary>
public class PipelineCodeReviewSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;
    private PipelineConfiguration? _saved;

    public PipelineCodeReviewSectionComponentTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                _saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });
    }

    private IRenderedComponent<PipelineCodeReviewSection> RenderSection(Action<(string, bool)>? onStatus = null) =>
        Render<PipelineCodeReviewSection>(p =>
        {
            p.Add(s => s.ConfigClient, _mockStore.Object);
            if (onStatus is not null)
                p.Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, onStatus));
        });

    private static async Task SaveAsync(IRenderedComponent<PipelineCodeReviewSection> cut)
    {
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Code Review"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    [Fact]
    public void RendersTheThreeBlocks()
    {
        var cut = RenderSection();

        Assert.Contains("Code Review", cut.Markup);
        Assert.Contains("Implementation Runs", cut.Markup);
        Assert.Contains("PR Reviews", cut.Markup);
        Assert.Contains("Acceptance Criteria", cut.Markup);
        Assert.Contains("Reviewer Configs", cut.Markup);
    }

    [Fact]
    public void RendersReviewFields()
    {
        var cut = RenderSection();

        Assert.Contains("Review Implementation Runs", cut.Markup);
        Assert.Contains("Max Review Iterations", cut.Markup);
        Assert.Contains("Fix Prompt", cut.Markup);
        Assert.Contains("Enable Inline Review Comments", cut.Markup);
        Assert.Contains("Minimum Severity", cut.Markup);
        Assert.Contains("Maximum Inline Comments", cut.Markup);
        Assert.Contains("Enable Acceptance Criteria Check", cut.Markup);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                CodeReview = new CodeReviewConfiguration
                {
                    MaxIterations = 4,
                    InlineComments = new InlineCommentSettings { MaxInlineComments = 25 },
                }
            });

        var cut = RenderSection();

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "25");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "4");
    }

    [Fact]
    public async Task Save_PersistsInlineCommentAndAcceptanceCriteriaSettings()
    {
        var cut = RenderSection();

        await SaveAsync(cut);

        Assert.NotNull(_saved);
        Assert.True(_saved!.CodeReview.InlineComments.Enabled);
        Assert.Equal(FindingSeverity.Warning, _saved.CodeReview.InlineComments.SeverityThreshold);
        Assert.Equal(15, _saved.CodeReview.InlineComments.MaxInlineComments);
        Assert.True(_saved.CodeReview.InlineComments.OrderBySeverity);
        Assert.Equal(1, _saved.CodeReview.InlineComments.MaxRetries);
        Assert.True(_saved.AcceptanceCriteriaEnabled);
        Assert.Equal(DefaultPrompts.AcceptanceCriteriaCompliance, _saved.AcceptanceCriteriaPrompt);
    }

    [Fact]
    public async Task Save_WithImplementationReviewOff_StoresZeroIterations()
    {
        var cut = RenderSection();
        cut.Find($"[data-setting='CodeReview.MaxIterations'] input[type='checkbox']").Change(false);

        await SaveAsync(cut);

        Assert.Equal(0, _saved!.CodeReview.MaxIterations);
    }

    [Fact]
    public async Task Save_WithImplementationReviewOn_StoresTheIterations()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { CodeReview = new CodeReviewConfiguration { MaxIterations = 3 } });
        var cut = RenderSection();

        await SaveAsync(cut);

        Assert.Equal(3, _saved!.CodeReview.MaxIterations);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = RenderSection(v => status = v);

        await SaveAsync(cut);

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }
}
