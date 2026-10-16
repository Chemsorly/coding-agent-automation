using AwesomeAssertions;
using CodingAgent.Api.Consolidation;
using CodingAgent.Contracts.Ui;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Consolidation;

/// <summary>
/// Spec 051 2.4b: what the Consolidation page shows, worked out in the API from the stored templates,
/// projects and settings. UiConsolidationEndpointTests cover access and the trigger.
/// </summary>
public sealed class ConsolidationPageServiceTests
{
    private static PipelineJobTemplate Template(string id, string? brain = "brain-1", string? repo = "repo-1", string? issues = "issues-1",
        bool enabled = true, bool brainReadOnly = false) => new()
        {
            Id = id, Name = $"{id}-name", BrainProviderId = brain, RepoProviderId = repo ?? "", IssueProviderId = issues ?? "",
            Enabled = enabled, BrainReadOnly = brainReadOnly,
        };

    private static PipelineProject Project(string name, bool enabled = true, bool? brainReadOnly = null, params string[] templateIds) => new()
    {
        Id = $"{name}-id", Name = name, Enabled = enabled, BrainReadOnly = brainReadOnly, TemplateIds = templateIds,
    };

    [Fact]
    public void Overview_ListsTheEnabledTemplatesOfEnabledProjects_ByProjectName()
    {
        var projects = new[]
        {
            Project("zulu", templateIds: ["z1"]),
            Project("alpha", templateIds: ["a1", "a2", "missing"]),
            Project("off", enabled: false, templateIds: ["o1"]),
        };
        var templates = new[] { Template("z1"), Template("a1"), Template("a2", enabled: false), Template("o1") };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, new PipelineConfiguration(), null);

        overview.Templates.Select(t => t.Id).Should().Equal("a1", "z1");
    }

    [Fact]
    public void Overview_SaysWhatEachTemplateSupports()
    {
        var projects = new[] { Project("alpha", templateIds: ["full", "no-brain", "no-issues"]) };
        var templates = new[] { Template("full"), Template("no-brain", brain: null), Template("no-issues", issues: null) };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, new PipelineConfiguration(), null);

        overview.Templates.Should().Equal(
            new UiConsolidationTemplate("full", "full-name", true, true, false),
            new UiConsolidationTemplate("no-brain", "no-brain-name", false, true, false),
            new UiConsolidationTemplate("no-issues", "no-issues-name", true, false, false));
    }

    [Fact]
    public void Overview_BrainIsReadOnly_ByTheTemplatesFlag_OrTheGlobalSettingWithTheProjectsOverride()
    {
        var projects = new[]
        {
            Project("alpha", templateIds: ["own-flag", "global"]),
            Project("beta", brainReadOnly: false, templateIds: ["project-writes"]),
        };
        var templates = new[] { Template("own-flag", brainReadOnly: true), Template("global"), Template("project-writes") };
        var readOnlyGlobally = new PipelineConfiguration { BrainReadOnly = true };

        var overview = ConsolidationPageService.BuildOverview(projects, templates, readOnlyGlobally, null);

        overview.Templates.ToDictionary(t => t.Id, t => t.BrainReadOnly).Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["own-flag"] = true, ["global"] = true, ["project-writes"] = false,
        });
    }

    [Fact]
    public void Overview_MapsEveryTemplateWithABrain_AndPassesTheSuggestionsThrough()
    {
        var suggestions = new HarnessSuggestions { GeneratedAtUtc = DateTime.UtcNow, BasedOnRunCount = 4, SuccessRate = 0.5m, Suggestions = [] };
        var templates = new[] { Template("enabled"), Template("disabled", enabled: false, brain: "brain-2"), Template("brainless", brain: null) };

        var overview = ConsolidationPageService.BuildOverview([], templates, new PipelineConfiguration(), suggestions);

        overview.BrainProviderByTemplate.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["enabled"] = "brain-1", ["disabled"] = "brain-2",
        });
        overview.HarnessSuggestions.Should().BeSameAs(suggestions);
    }

    [Fact]
    public void RefactoringScan_WithoutAProject_UsesTheGlobalSettings()
    {
        var config = new PipelineConfiguration { MaxRefactoringProposals = 5, RefactoringReviewEnabled = false, HotspotAnalysisLookback = TimeSpan.FromDays(30) };

        ConsolidationPageService.BuildRefactoringScanSettings(config, null)
            .Should().Be(new UiRefactoringScanSettings(5, false, 30, false, false));
    }

    [Fact]
    public void RefactoringScan_MarksAProjectOverride_OnlyWhenItChangesTheValue()
    {
        var config = new PipelineConfiguration { MaxRefactoringProposals = 3, RefactoringReviewEnabled = true };
        var overriding = new PipelineProject { Id = "p", Name = "p", MaxRefactoringProposals = 7, RefactoringReviewEnabled = true };

        ConsolidationPageService.BuildRefactoringScanSettings(config, overriding)
            .Should().Be(new UiRefactoringScanSettings(7, true, 90, true, false));
    }

    // ── GetAutoCountersAsync ──────────────────────────────────────────────────────

    private static ConsolidationPageService BuildService(
        Mock<ConsolidationStalenessService> mockStaleness,
        PipelineConfiguration config,
        PipelineProject[] projects,
        PipelineJobTemplate[] templates)
    {
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>(projects));
        mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>(templates));

        return new ConsolidationPageService(
            mockProjectStore.Object,
            mockConfigStore.Object,
            new Mock<IConsolidationService>().Object,
            mockStaleness.Object);
    }

    [Fact]
    public async Task GetAutoCounters_AllThresholdsZero_ReturnsEmptyCounters()
    {
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        var service = BuildService(mockStaleness, new PipelineConfiguration(),
            [Project("p", templateIds: ["t1"])],
            [Template("t1")]);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.BrainCounters.Should().BeEmpty();
        result.RefactoringCounters.Should().BeEmpty();
        result.HarnessCounter.Should().BeNull();
        mockStaleness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetAutoCounters_BrainThresholdSet_ReturnsBrainCounter()
    {
        var config = new PipelineConfiguration { AutoBrainConsolidationAfterRuns = 20 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.CountBrainRunsSinceLastConsolidationAsync("brain-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(14);
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = BuildService(mockStaleness, config,
            [Project("p", templateIds: ["t1"])],
            [Template("t1", brain: "brain-1")]);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.BrainCounters.Should().ContainKey("t1")
            .WhoseValue.Should().Be(new UiAutoCounter(14, 20, null));
    }

    [Fact]
    public async Task GetAutoCounters_RefactoringThresholdSet_NotPaused_NotBackpressured_ReturnsCounter()
    {
        var config = new PipelineConfiguration { AutoRefactoringScanAfterClosedIssues = 10, MaxRefactoringProposals = 3 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockStaleness
            .Setup(s => s.CountOpenScanIssuesAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);  // below limit — not backpressured
        mockStaleness
            .Setup(s => s.CountClosedIssuesSinceLastScanAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var service = BuildService(mockStaleness, config,
            [Project("p", templateIds: ["t1"])],
            [Template("t1", brain: null)]);  // no brain, has repo + issues

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.RefactoringCounters.Should().ContainKey("t1")
            .WhoseValue.Should().Be(new UiAutoCounter(7, 10, null));
    }

    [Fact]
    public async Task GetAutoCounters_RefactoringPaused_ReturnsPausedBlockedText()
    {
        var config = new PipelineConfiguration { AutoRefactoringScanAfterClosedIssues = 10, MaxRefactoringProposals = 3 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockStaleness
            .Setup(s => s.CountClosedIssuesSinceLastScanAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var service = BuildService(mockStaleness, config,
            [Project("p", templateIds: ["t1"])],
            [Template("t1", brain: null)]);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.RefactoringCounters.Should().ContainKey("t1")
            .WhoseValue.Blocked.Should().Be("auto paused: the last run failed");
    }

    [Fact]
    public async Task GetAutoCounters_RefactoringBackpressure_ReturnsWaitingBlockedText()
    {
        var config = new PipelineConfiguration { AutoRefactoringScanAfterClosedIssues = 10, MaxRefactoringProposals = 3 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockStaleness
            .Setup(s => s.CountOpenScanIssuesAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);  // >= MaxRefactoringProposals (3)
        mockStaleness
            .Setup(s => s.CountClosedIssuesSinceLastScanAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var service = BuildService(mockStaleness, config,
            [Project("p", templateIds: ["t1"])],
            [Template("t1", brain: null)]);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.RefactoringCounters.Should().ContainKey("t1")
            .WhoseValue.Blocked.Should().Be("auto waiting: 3 scan issues still open");
    }

    [Fact]
    public async Task GetAutoCounters_HarnessThresholdSet_ReturnsHarnessCounter()
    {
        var config = new PipelineConfiguration { AutoHarnessSuggestionsAfterFeedback = 50 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.CountFeedbackSinceLastHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(41);
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = BuildService(mockStaleness, config, [], []);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.HarnessCounter.Should().Be(new UiAutoCounter(41, 50, null));
    }

    [Fact]
    public async Task GetAutoCounters_HarnessPaused_ReturnsPausedBlockedText()
    {
        var config = new PipelineConfiguration { AutoHarnessSuggestionsAfterFeedback = 50 };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.CountFeedbackSinceLastHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(30);
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = BuildService(mockStaleness, config, [], []);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.HarnessCounter!.Blocked.Should().Be("auto paused: the last run failed");
    }

    [Fact]
    public async Task GetAutoCounters_StalenessThrows_OmitsThatCounterAndContinues()
    {
        var config = new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 20,
            AutoHarnessSuggestionsAfterFeedback = 50
        };
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        // Brain throws
        mockStaleness
            .Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("staleness failure"));
        // Harness succeeds
        mockStaleness
            .Setup(s => s.CountFeedbackSinceLastHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(20);
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = BuildService(mockStaleness, config,
            [Project("p", templateIds: ["t1"])],
            [Template("t1", brain: "brain-1", repo: null, issues: null)]);

        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        result.BrainCounters.Should().BeEmpty("brain counter failed");
        result.HarnessCounter.Should().Be(new UiAutoCounter(20, 50, null));
    }
}
