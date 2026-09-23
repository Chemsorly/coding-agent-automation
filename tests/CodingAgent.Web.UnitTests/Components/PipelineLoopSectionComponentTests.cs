using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Structural tests for PipelineLoopSection toggle markup.
///
/// These tests verify that the HTML structure expected by the toggle-switch CSS rules
/// is present in the rendered output. bUnit does not apply CSS stylesheets, so they
/// cannot detect CSS scoping issues directly, but they guard the markup contract:
/// if the classes disappear, the toggle will always render as a native checkbox.
///
/// See issue #2938: Blazor scoped CSS in AgentCoding.razor.css does not cross
/// component boundaries into PipelineLoopSection — requiring its own .razor.css.
/// </summary>
public class PipelineLoopSectionComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient;

    public PipelineLoopSectionComponentTests()
    {
        _mockConfigClient = new Mock<IPipelineApiConfigClient>();
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigClient
            .Setup(c => c.UpdatePipelineConfigAsync(
                It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private IRenderedComponent<PipelineLoopSection> RenderSection() =>
        Render<PipelineLoopSection>(p => p.Add(s => s.ConfigClient, _mockConfigClient.Object));

    /// <summary>
    /// The Queue Sweep toggle in PipelineLoopSection must use the custom
    /// toggle-switch/toggle-slider markup so it renders as a switch control.
    /// Regression guard for issue #2938.
    /// </summary>
    [Fact]
    public void QueueSweepToggle_RendersAsToggleSwitch()
    {
        var cut = RenderSection();

        // TODO: String substring match on the full HTML blob is weaker than a DOM query —
        // a class name in a comment, data attribute, or unrelated element would make this
        // assertion pass even if the actual toggle lost its class. QueueSweepToggle_HasCorrectDomStructure
        // already validates the DOM structure precisely; consider removing or replacing
        // these markup string checks with structural assertions. (review finding #2938)
        Assert.Contains("toggle-switch", cut.Markup);
        Assert.Contains("toggle-slider", cut.Markup);
    }

    /// <summary>
    /// The Queue Sweep toggle must be a &lt;label class="toggle-switch"&gt; wrapping
    /// a hidden checkbox and a toggle-slider span.
    /// </summary>
    [Fact]
    public void QueueSweepToggle_HasCorrectDomStructure()
    {
        var cut = RenderSection();

        var toggleLabel = cut.Find("label.toggle-switch");
        Assert.NotNull(toggleLabel);

        var input = toggleLabel.QuerySelector("input[type='checkbox']");
        Assert.NotNull(input);

        var slider = toggleLabel.QuerySelector("span.toggle-slider");
        Assert.NotNull(slider);
    }
}
