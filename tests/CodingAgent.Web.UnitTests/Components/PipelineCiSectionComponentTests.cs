using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for PipelineCiSection (Settings → Global Defaults → External CI).
/// </summary>
public class PipelineCiSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockStore;

    public PipelineCiSectionComponentTests()
    {
        _mockStore = new Mock<IPipelineApiConfigClient>();
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void RendersHeaderAndCiFields()
    {
        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object));

        Assert.Equal("External CI", cut.Find("h2").TextContent);
        Assert.Equal(
            [
                nameof(PipelineConfiguration.ExternalCiTimeout),
                nameof(PipelineConfiguration.ExternalCiPollInterval),
                nameof(PipelineConfiguration.CiNotStartedTimeout),
                nameof(PipelineConfiguration.CiNotStartedMaxRetries),
            ],
            SettingsOnThePage(cut));
    }

    [Fact]
    public void AdvancedExpanded_AddsBranchMoveRePolls_AndNoCodeReviewSetting()
    {
        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object));
        cut.Find(".advanced-toggle").Click();

        Assert.Equal(
            [
                nameof(PipelineConfiguration.ExternalCiTimeout),
                nameof(PipelineConfiguration.ExternalCiPollInterval),
                nameof(PipelineConfiguration.CiNotStartedTimeout),
                nameof(PipelineConfiguration.CiNotStartedMaxRetries),
                nameof(PipelineConfiguration.CiCancelledMoveMaxRetries),
            ],
            SettingsOnThePage(cut));
    }

    [Fact]
    public void InputLimits_ComeFromTheSettingRanges()
    {
        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object));

        var retries = cut.Find($"[data-setting='{nameof(PipelineConfiguration.CiNotStartedMaxRetries)}'] input");
        Assert.Equal("0", retries.GetAttribute("min"));
        Assert.Equal("20", retries.GetAttribute("max"));
    }

    [Fact]
    public async Task Save_PersistsCiSettings()
    {
        PipelineConfiguration? saved = null;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) =>
            {
                saved = transform(new PipelineConfiguration());
                return Task.CompletedTask;
            });
        _mockStore.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { CiCancelledMoveMaxRetries = 6, ExternalCiTimeout = TimeSpan.FromMinutes(40) });

        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object));
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save External CI"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(saved);
        Assert.Equal(6, saved!.CiCancelledMoveMaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(40), saved.ExternalCiTimeout);
    }

    [Fact]
    public async Task Save_InvokesOnShowStatus_WithSuccess()
    {
        (string Message, bool IsError) status = default;
        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save External CI"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    [Fact]
    public async Task Save_WhenTheApiRejectsAValue_ShowsTheReason()
    {
        (string Message, bool IsError) status = default;
        _mockStore.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("CiNotStartedMaxRetries must be between 0 and 20 (was 25)."));
        var cut = Render<PipelineCiSection>(p =>
            p.Add(s => s.ConfigClient, _mockStore.Object)
             .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save External CI"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(status.IsError);
        Assert.Contains("CiNotStartedMaxRetries must be between 0 and 20", status.Message);
    }

    /// <summary>The settings the page offers, by the <c>data-setting</c> anchors the settings coverage guard also uses.</summary>
    private static IEnumerable<string> SettingsOnThePage(IRenderedComponent<PipelineCiSection> cut) =>
        cut.FindAll("[data-setting]").Select(e => e.GetAttribute("data-setting")!);
}
