using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the pipeline settings section components:
/// PipelineGeneralSection, PipelineLoopSection, PipelinePromptsSection, PipelineSecuritySection.
/// These are simple form components that load/save PipelineConfiguration fields.
/// </summary>
public class PipelineSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ═══ PipelineGeneralSection ═══

    [Fact]
    public void GeneralSection_RendersHeader()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("General", cut.Markup);
    }

    [Fact]
    public void GeneralSection_RendersMaxRetriesInput()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Max Retries", cut.Markup);
        Assert.NotNull(cut.Find("input[type='number']"));
    }

    [Fact]
    public void GeneralSection_RendersAgentTimeoutInput()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Agent Timeout", cut.Markup);
    }

    [Fact]
    public void GeneralSection_LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { MaxRetries = 5, AgentTimeout = TimeSpan.FromMinutes(45) });

        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "5");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "45");
    }

    [Fact]
    public void GeneralSection_RendersSaveButton()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Save General Settings", cut.Markup);
    }

    [Fact]
    public async Task GeneralSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineGeneralSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save General"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeneralSection_Save_InvokesOnShowStatus()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineGeneralSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
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
            p.Add(s => s.ConfigStore, _mockStore.Object)
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
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Pipeline Loop", cut.Markup);
    }

    [Fact]
    public void LoopSection_RendersAllFields()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Poll Interval", cut.Markup);
        Assert.Contains("Max Runs Per Cycle", cut.Markup);
        Assert.Contains("Max Consecutive Poll Failures", cut.Markup);
        Assert.Contains("Max Backoff Interval", cut.Markup);
    }

    [Fact]
    public void LoopSection_LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
                ClosedLoopMaxRunsPerCycle = 5,
                ClosedLoopMaxConsecutivePollFailures = 10,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(1800)
            });

        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var inputs = cut.FindAll("input[type='number']");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "120");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "5");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "10");
        Assert.Contains(inputs, i => i.GetAttribute("value") == "1800");
    }

    [Fact]
    public async Task LoopSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Pipeline Loop"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void LoopSection_RendersHintIcons()
    {
        var cut = Render<PipelineLoopSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        var hints = cut.FindAll(".form-hint-icon");
        Assert.Equal(4, hints.Count);
    }

    // ═══ PipelinePromptsSection ═══

    [Fact]
    public void PromptsSection_RendersHeader()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Prompts", cut.Markup);
    }

    [Fact]
    public void PromptsSection_RendersTextareas()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Analysis Prompt", cut.Markup);
        Assert.Contains("Implementation Prompt", cut.Markup);
        var textareas = cut.FindAll("textarea");
        Assert.Equal(2, textareas.Count);
    }

    [Fact]
    public void PromptsSection_RendersResetButtons()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        var resetButtons = cut.FindAll(".btn-revert");
        Assert.Equal(2, resetButtons.Count);
    }

    [Fact]
    public void PromptsSection_ResetButtons_DisabledWhenDefault()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        var resetButtons = cut.FindAll(".btn-revert");
        Assert.All(resetButtons, btn => Assert.True(btn.HasAttribute("disabled")));
    }

    [Fact]
    public async Task PromptsSection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelinePromptsSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Prompt"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ PipelineSecuritySection ═══

    [Fact]
    public void SecuritySection_RendersHeader()
    {
        var cut = Render<PipelineSecuritySection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Security", cut.Markup);
    }

    [Fact]
    public void SecuritySection_RendersAllFields()
    {
        var cut = Render<PipelineSecuritySection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Blacklisted Paths", cut.Markup);
        Assert.Contains("Blacklist Mode", cut.Markup);
        Assert.Contains("Delete Workspace After Successful PR", cut.Markup);
        Assert.Contains("Failed Run Workspace Retention", cut.Markup);
        Assert.Contains("Brain Repository Read-Only", cut.Markup);
    }

    [Fact]
    public void SecuritySection_LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                BlacklistedPaths = new[] { ".secret", ".env" },
                BlacklistMode = BlacklistMode.Fail,
                CleanupSuccessfulWorkspaces = false,
                FailedWorkspaceRetentionDays = 14,
                BrainReadOnly = true
            });

        var cut = Render<PipelineSecuritySection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains(".secret, .env", cut.Markup);
    }

    [Fact]
    public void SecuritySection_RendersBlacklistModeDropdown()
    {
        var cut = Render<PipelineSecuritySection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));
        var select = cut.Find("select");
        Assert.NotNull(select);
        Assert.Contains("Warn", cut.Markup);
        Assert.Contains("Fail", cut.Markup);
    }

    [Fact]
    public async Task SecuritySection_Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineSecuritySection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Security"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecuritySection_SaveFails_ShowsError()
    {
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permission denied"));

        (string Message, bool IsError) status = default;
        var cut = Render<PipelineSecuritySection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Security"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("permission denied", status.Message);
        Assert.True(status.IsError);
    }
}
