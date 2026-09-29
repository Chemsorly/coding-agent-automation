using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineAdvancedSection.
/// </summary>
public class PipelineAdvancedSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;
    private PipelineConfiguration? _saved;

    public PipelineAdvancedSectionComponentTests()
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

    private IRenderedComponent<PipelineAdvancedSection> RenderSection(Action<(string, bool)>? onStatus = null) =>
        Render<PipelineAdvancedSection>(p =>
        {
            p.Add(s => s.ConfigClient, _mockStore.Object);
            if (onStatus is not null)
                p.Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, onStatus));
        });

    private static async Task SaveAsync(IRenderedComponent<PipelineAdvancedSection> cut)
    {
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    [Fact]
    public void RendersAllSections()
    {
        var cut = RenderSection();

        Assert.Contains("Agent Routing", cut.Markup);
        Assert.Contains("Brain Repository", cut.Markup);
        Assert.Contains("Issue Images", cut.Markup);
        Assert.Contains("Data Retention", cut.Markup);
        Assert.Contains("Comment Delivery", cut.Markup);
        Assert.DoesNotContain("Heartbeat", cut.Markup);
        Assert.DoesNotContain("Buffer Capacities", cut.Markup);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                DefaultRequiredAgentLabels = "kiro,dotnet",
                BrainPushMaxRetries = 5,
                MaxIssueImages = 12,
                MaxImageSizeBytes = 8 * 1_048_576,
                PipelineRunRetentionCount = 300,
                FeedbackCommentOutboxMaxAttempts = 9,
            });

        var cut = RenderSection();

        Assert.Equal("kiro,dotnet", cut.Find("input[type='text']").GetAttribute("value"));
        var numberInputs = cut.FindAll("input[type='number']");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "5");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "12");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "8");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "300");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "9");
    }

    [Fact]
    public void LoadsNullLabels_AsEmptyString()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { DefaultRequiredAgentLabels = null });

        var cut = RenderSection();

        Assert.Equal("", cut.Find("input[type='text']").GetAttribute("value"));
    }

    [Fact]
    public void LegacyRetentionMinusOne_ShowsAsZero()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { PipelineRunRetentionCount = -1, WorkItemRetentionCount = -1 });

        var cut = RenderSection();

        var runs = cut.Find($"[data-setting='{nameof(PipelineConfiguration.PipelineRunRetentionCount)}'] input");
        Assert.Equal("0", runs.GetAttribute("value"));
    }

    [Fact]
    public async Task Save_PersistsTheSettings()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                BrainReadOnly = true,
                EnableNativeImageParts = false,
                MaxTotalImageSizeBytes = 30 * 1_048_576,
                WorkItemRetentionCount = 500,
                FeedbackCommentOutboxMaxAttempts = 7,
            });
        var cut = RenderSection();

        await SaveAsync(cut);

        Assert.NotNull(_saved);
        Assert.Null(_saved!.DefaultRequiredAgentLabels);
        Assert.Equal(3, _saved.BrainPushMaxRetries);
        Assert.True(_saved.BrainReadOnly);
        Assert.False(_saved.EnableNativeImageParts);
        Assert.Equal(30 * 1_048_576, _saved.MaxTotalImageSizeBytes);
        Assert.Equal(500, _saved.WorkItemRetentionCount);
        Assert.Equal(0, _saved.PipelineRunRetentionCount);
        Assert.Equal(7, _saved.FeedbackCommentOutboxMaxAttempts);
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

    [Fact]
    public async Task SaveFails_ShowsError()
    {
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permission denied"));
        (string Message, bool IsError) status = default;
        var cut = RenderSection(v => status = v);

        await SaveAsync(cut);

        Assert.Contains("permission denied", status.Message);
        Assert.True(status.IsError);
    }
}
