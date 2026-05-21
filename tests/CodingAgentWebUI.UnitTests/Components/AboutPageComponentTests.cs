using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class AboutPageComponentTests : BunitContext
{
    private void RegisterDefaults(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        Services.AddSingleton(CreateService(history));
        Services.AddSingleton(new BuildInfo());
    }

    private PipelineOrchestrationService CreateService(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        var store = new Mock<IConfigurationStore>();
        store.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var factory = new Mock<IProviderFactory>();
        var validator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory())
            .Returns(history ?? Array.Empty<PipelineRunSummary>());

        return new PipelineOrchestrationService(
            store.Object, factory.Object, new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(Log.Logger),
            new QualityGateExecutor(validator.Object, new PullRequestOrchestrator(Log.Logger), Log.Logger),
            Log.Logger,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: mockHistory.Object);
    }

    [Fact]
    public void Renders_IntroSection()
    {
        RegisterDefaults();
        var cut = Render<About>();

        cut.Find(".about-oneliner").MarkupMatches(
            "<p class=\"about-oneliner\">Automated development pipeline powered by coding agents</p>");
        Assert.NotEmpty(cut.Find(".about-description").TextContent);
    }

    [Fact]
    public void Renders_VersionInfo()
    {
        RegisterDefaults();
        var cut = Render<About>();

        var values = cut.FindAll(".about-value");
        Assert.True(values.Count >= 2);
        Assert.NotEmpty(values[0].TextContent); // app version
        Assert.Contains(".NET", values[1].TextContent); // runtime
    }

    [Fact]
    public void Renders_EmptyStats_WhenNoHistory()
    {
        RegisterDefaults();
        var cut = Render<About>();

        Assert.Equal("No pipeline runs yet.", cut.Find(".about-muted").TextContent);
    }

    [Fact]
    public void Renders_Links()
    {
        RegisterDefaults();
        var cut = Render<About>();

        var links = cut.FindAll(".about-links a");
        Assert.Equal(2, links.Count);
        Assert.Equal("https://github.com/Chemsorly/coding-agent-automation", links[0].GetAttribute("href"));
        Assert.Equal("https://kiro.dev/docs/cli/", links[1].GetAttribute("href"));
    }

    [Fact]
    public void Renders_PipelineStats_WithHistory()
    {
        var now = DateTime.UtcNow;
        var summaries = new List<PipelineRunSummary>
        {
            new() { RunId = "1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed, StartedAt = now.AddMinutes(-30), CompletedAt = now.AddMinutes(-20) },
            new() { RunId = "2", IssueIdentifier = "2", IssueTitle = "B",
                FinalStep = PipelineStep.Failed, StartedAt = now.AddMinutes(-50), CompletedAt = now.AddMinutes(-45) },
            new() { RunId = "3", IssueIdentifier = "3", IssueTitle = "C",
                FinalStep = PipelineStep.Cancelled, StartedAt = now.AddMinutes(-60), CompletedAt = now.AddMinutes(-58) }
        };

        RegisterDefaults(summaries);
        var cut = Render<About>();

        // Should NOT show the empty-state message
        Assert.Empty(cut.FindAll(".about-muted"));

        // Stats grid is the third .about-info-grid (first is version info, second is build info)
        var statsValues = cut.FindAll(".about-info-grid")[2].QuerySelectorAll(".about-value");
        Assert.Equal("3", statsValues[0].TextContent);  // total
        Assert.Equal("1", statsValues[1].TextContent);  // success
        Assert.Equal("1", statsValues[2].TextContent);  // failed
        Assert.Equal("1", statsValues[3].TextContent);  // cancelled
        Assert.NotEqual("—", statsValues[4].TextContent); // avg duration is computed
        Assert.NotEqual("—", statsValues[5].TextContent); // last run is populated
    }

    [Fact]
    public void Renders_TechBadges()
    {
        RegisterDefaults();
        var cut = Render<About>();

        var badges = cut.FindAll(".tech-badge");
        Assert.Equal(4, badges.Count);
        Assert.Equal(".NET 10", badges[0].TextContent);
        Assert.Equal("Blazor Server", badges[1].TextContent);
        Assert.Equal("Kiro CLI", badges[2].TextContent);
        Assert.Equal("Docker", badges[3].TextContent);
    }
}
