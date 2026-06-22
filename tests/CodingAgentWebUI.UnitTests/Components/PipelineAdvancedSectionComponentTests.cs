using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineAdvancedSection.
/// </summary>
public class PipelineAdvancedSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public PipelineAdvancedSectionComponentTests()
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
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Advanced", cut.Markup);
    }

    [Fact]
    public void RendersAllSections()
    {
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Agent Routing", cut.Markup);
        Assert.Contains("Brain Repository", cut.Markup);
        Assert.Contains("Agent Health Monitoring", cut.Markup);
        Assert.Contains("Buffer Capacities", cut.Markup);
    }

    [Fact]
    public void RendersAllFields()
    {
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));
        Assert.Contains("Default Required Agent Labels", cut.Markup);
        Assert.Contains("Brain Push Max Retries", cut.Markup);
        Assert.Contains("Agent Disconnect Grace Period", cut.Markup);
        Assert.Contains("Agent Busy Progress Timeout", cut.Markup);
        Assert.Contains("Heartbeat Sweep Interval", cut.Markup);
        Assert.Contains("Heartbeat Timeout", cut.Markup);
        Assert.Contains("Output Buffer Capacity", cut.Markup);
        Assert.Contains("Output Lines Capacity", cut.Markup);
        Assert.Contains("Chat History Capacity", cut.Markup);
        Assert.Contains("Quality Gate History Capacity", cut.Markup);
        Assert.Contains("Retry Errors Capacity", cut.Markup);
    }

    [Fact]
    public void LoadsConfigValues()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                DefaultRequiredAgentLabels = "kiro,dotnet",
                BrainPushMaxRetries = 5,
                AgentDisconnectGracePeriod = TimeSpan.FromMinutes(10),
                AgentBusyProgressTimeout = TimeSpan.FromMinutes(90),
                HeartbeatSweepIntervalSeconds = 45,
                HeartbeatTimeoutSeconds = 120,
                OutputBufferCapacity = 20000,
                OutputLinesCapacity = 8000,
                ChatHistoryCapacity = 300,
                QualityGateHistoryCapacity = 75,
                RetryErrorsCapacity = 200
            });

        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var textInput = cut.Find("input[type='text']");
        Assert.Equal("kiro,dotnet", textInput.GetAttribute("value"));

        var numberInputs = cut.FindAll("input[type='number']");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "5");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "10");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "90");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "45");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "120");
        Assert.Contains(numberInputs, i => i.GetAttribute("value") == "20000");
    }

    [Fact]
    public void LoadsNullLabels_AsEmptyString()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { DefaultRequiredAgentLabels = null });

        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var textInput = cut.Find("input[type='text']");
        Assert.Equal("", textInput.GetAttribute("value"));
    }

    [Fact]
    public async Task Save_CallsUpdatePipelineConfig()
    {
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.UpdatePipelineConfigAsync(
            It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public async Task Save_PersistsDefaultValues()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Null(saved!.DefaultRequiredAgentLabels);
        Assert.Equal(3, saved.BrainPushMaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(5), saved.AgentDisconnectGracePeriod);
        Assert.Equal(TimeSpan.FromMinutes(60), saved.AgentBusyProgressTimeout);
        Assert.Equal(60, saved.HeartbeatSweepIntervalSeconds);
        Assert.Equal(90, saved.HeartbeatTimeoutSeconds);
        Assert.Equal(10000, saved.OutputBufferCapacity);
        Assert.Equal(5000, saved.OutputLinesCapacity);
        Assert.Equal(200, saved.ChatHistoryCapacity);
        Assert.Equal(50, saved.QualityGateHistoryCapacity);
        Assert.Equal(100, saved.RetryErrorsCapacity);
    }

    [Fact]
    public async Task Save_EmptyLabels_PersistsAsNull()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });

        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Null(saved!.DefaultRequiredAgentLabels);
    }

    [Fact]
    public async Task SaveFails_ShowsError()
    {
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permission denied"));

        (string Message, bool IsError) status = default;
        var cut = Render<PipelineAdvancedSection>(p =>
            p.Add(s => s.ConfigStore, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Advanced"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("permission denied", status.Message);
        Assert.True(status.IsError);
    }
}
