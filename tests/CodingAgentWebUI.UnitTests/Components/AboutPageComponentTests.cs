using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

public class AboutPageComponentTests : BunitContext
{
    private void RegisterDefaults(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(history ?? Array.Empty<PipelineRunSummary>());
        Services.AddSingleton(mockHistory.Object);
        Services.AddSingleton(new BuildInfo());
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
        var nowOffset = DateTimeOffset.UtcNow;
        var summaries = new List<PipelineRunSummary>
        {
            new() { RunId = "1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed, StartedAt = now.AddMinutes(-30), CompletedAt = now.AddMinutes(-20),
                StartedAtOffset = nowOffset.AddMinutes(-30), CompletedAtOffset = nowOffset.AddMinutes(-20) },
            new() { RunId = "2", IssueIdentifier = "2", IssueTitle = "B",
                FinalStep = PipelineStep.Failed, StartedAt = now.AddMinutes(-50), CompletedAt = now.AddMinutes(-45),
                StartedAtOffset = nowOffset.AddMinutes(-50), CompletedAtOffset = nowOffset.AddMinutes(-45) },
            new() { RunId = "3", IssueIdentifier = "3", IssueTitle = "C",
                FinalStep = PipelineStep.Cancelled, StartedAt = now.AddMinutes(-60), CompletedAt = now.AddMinutes(-58),
                StartedAtOffset = nowOffset.AddMinutes(-60), CompletedAtOffset = nowOffset.AddMinutes(-58) }
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
    public void LastRun_ShowsDash_WhenStartedAtOffsetIsDefault()
    {
        // Legacy records may have StartedAtOffset == default(DateTimeOffset) (year 0001).
        // The page must display "—" instead of rendering an invalid date.
        var summaries = new List<PipelineRunSummary>
        {
            new() { RunId = "1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed,
                StartedAt = default,
                StartedAtOffset = default   // simulates legacy record with no offset stored
            }
        };

        RegisterDefaults(summaries);
        var cut = Render<About>();

        var statsValues = cut.FindAll(".about-info-grid")[2].QuerySelectorAll(".about-value");
        // Last Run is the 6th value (index 5) in the stats grid
        Assert.Equal("—", statsValues[5].TextContent);
    }

    [Fact]
    public void LastRun_ShowsFormattedDate_WhenStartedAtOffsetIsNonDefault()
    {
        var nowOffset = new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero);
        var summaries = new List<PipelineRunSummary>
        {
            new() { RunId = "1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed,
                StartedAt = nowOffset.UtcDateTime,
                StartedAtOffset = nowOffset
            }
        };

        RegisterDefaults(summaries);
        var cut = Render<About>();

        var statsValues = cut.FindAll(".about-info-grid")[2].QuerySelectorAll(".about-value");
        // Last Run should be a non-empty, non-dash formatted date string
        // TODO: Strengthen these assertions to Assert.Equal(nowOffset.ToLocalTime().ToString("g"), lastRunValue)
        // so a wrong format specifier, UTC vs local mismatch, or other formatting bug would be caught.
        var lastRunValue = statsValues[5].TextContent;
        Assert.NotEqual("—", lastRunValue);
        Assert.NotEmpty(lastRunValue);
        Assert.DoesNotContain("0001", lastRunValue);
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
