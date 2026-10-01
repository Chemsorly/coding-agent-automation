using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the pipeline settings section components:
/// PipelineGeneralSection, PipelineLoopSection, PipelinePromptsSection.
/// These are simple form components that load/save PipelineConfiguration fields.
/// </summary>
public class PipelineSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public PipelineSectionComponentTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ═══ PipelineGeneralSection ═══

    [Fact]
    public void GeneralSection_RendersHeader()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("General", cut.Markup);
    }

    [Fact]
    public void GeneralSection_RendersMaxRetriesInput()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Max Retries", cut.Markup);
        Assert.NotNull(cut.Find("input[type='number']"));
    }

    [Fact]
    public void GeneralSection_RendersAgentTimeoutInput()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Agent Timeout", cut.Markup);
    }

    [Fact]
    public void GeneralSection_LoadsConfigValues()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5, AgentTimeout = TimeSpan.FromMinutes(45) });

        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "5");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "45");
    }

    [Fact]
    public void GeneralSection_RendersSaveButton()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Save General Settings", cut.Markup);
    }

    [Fact]
    public async Task GeneralSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save General"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeneralSection_Save_InvokesOnShowStatus()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineGeneralSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save General"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public async Task GeneralSection_SaveFails_ShowsError()
    {
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk full"));

        (string Message, bool IsError) status = default;
        var cut = Render<PipelineGeneralSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save General"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("disk full", status.Message);
        Assert.True(status.IsError);
    }

    // ═══ PipelineLoopSection ═══

    [Fact]
    public void LoopSection_RendersHeader()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Pipeline Loop", cut.Markup);
    }

    [Fact]
    public void LoopSection_RendersAllFields()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Poll Interval", cut.Markup);
        Assert.Contains("Max Runs Per Cycle", cut.Markup);
        Assert.Contains("Advanced settings", cut.Markup);
    }

    [Fact]
    public void LoopSection_LoadsConfigValues()
    {
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
                ClosedLoopMaxRunsPerCycle = 5,
                ClosedLoopMaxConsecutivePollFailures = 10
            });

        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "120");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "5");
    }

    [Fact]
    public async Task LoopSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Pipeline Loop"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void LoopSection_RendersHintIcons()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        var hints = cut.FindAll(".form-hint-icon");
        Assert.Equal(4, hints.Count); // Poll Interval, Max Runs Per Cycle, Reserved Issue Slots, Queue Sweep (advanced fields hidden by default)
    }

    // ═══ PipelinePromptsSection ═══

    [Fact]
    public void PromptsSection_RendersHeader()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Prompts", cut.Markup);
    }

    [Fact]
    public void PromptsSection_RendersTextareas()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        Assert.Contains("Analysis Prompt", cut.Markup);
        Assert.Contains("Implementation Prompt", cut.Markup);
        var textareas = cut.FindAll("textarea");
        Assert.Equal(2, textareas.Count); // advanced fields hidden by default
    }

    [Fact]
    public void PromptsSection_RendersResetButtons()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        var resetButtons = cut.FindAll(".btn-revert");
        Assert.Equal(2, resetButtons.Count); // advanced fields hidden by default
    }

    [Fact]
    public void PromptsSection_ResetButtons_DisabledWhenDefault()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        var resetButtons = cut.FindAll(".btn-revert");
        Assert.Equal(2, resetButtons.Count);
        Assert.All(resetButtons, btn => Assert.True(btn.HasAttribute("disabled")));
    }

    [Fact]
    public async Task PromptsSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Prompt"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ PipelineGeneralSection — Relocated Settings ═══

    [Fact]
    public void GeneralSection_DoesNotOfferRemovedOrInternalSettings()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));
        cut.Find(".advanced-toggle").Click();

        Assert.DoesNotContain("Failed Run Workspace Retention", cut.Markup);
        Assert.DoesNotContain("Workspace Base Directory", cut.Markup);
        Assert.DoesNotContain("Issue Page Size", cut.Markup);
    }

    [Fact]
    public async Task GeneralSection_Save_IncludesCommitThresholdAndFeedbackTimeout()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AnalysisCommitThreshold = 45, FeedbackTimeoutSeconds = 120 });
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save General"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Equal(45, saved!.AnalysisCommitThreshold);
        Assert.Equal(120, saved.FeedbackTimeoutSeconds);
    }

    [Fact]
    public async Task LoopSection_Save_IncludesReservedIssueSlotsAndOrphanSweepInterval()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MinIssueSlots = 3, OrphanedLabelSweepIntervalMinutes = 45 });
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Pipeline Loop"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Equal(3, saved!.MinIssueSlots);
        Assert.Equal(45, saved.OrphanedLabelSweepIntervalMinutes);
    }
}
