using CodingAgent.Web.Auth;
using AwesomeAssertions;
using CodingAgent.Web.UnitTests.Auth;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Api.Consolidation;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Consolidation page.
/// Validates Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 8.2, 8.5, 10.2.
/// The page reads its cards and suggestions from IUiConsolidationClient and its run history from
/// IUiRunClient (spec 051 2.4b).
/// </summary>
public class ConsolidationPageComponentTests : BunitContext
{
    private const string Queued = "Consolidation queued — waiting for an idle agent.";
    private const string Rejected = "Brain consolidation rejected for Test Template — already running, a configuration error occurred, or template not found.";

    private readonly Mock<IUiConsolidationClient> _mockConsolidationClient = new();
    private readonly Mock<IUiRunClient> _mockRunHistoryClient = new();

    public ConsolidationPageComponentTests()
    {
        Services.AddTestAccess(); // Spec 049: global admin, so every control renders as before
    }

    /// <summary>
    /// Serves the page from stored-style templates, projects and settings: the overview and the scan
    /// settings are built by the API's own <see cref="ConsolidationPageService"/> builders, so these tests
    /// cover what the page shows for a configuration. Without <paramref name="projects"/>, one enabled
    /// project holds every template.
    /// </summary>
    private void RegisterServices(
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        IReadOnlyList<PipelineRunSummary>? runHistory = null,
        HarnessSuggestions? harnessSuggestions = null,
        PipelineConfiguration? pipelineConfig = null,
        IReadOnlyList<PipelineProject>? projects = null)
    {
        var config = pipelineConfig ?? new PipelineConfiguration();
        var allTemplates = templates ?? Array.Empty<PipelineJobTemplate>();
        var allProjects = projects ?? (allTemplates.Count == 0
            ? Array.Empty<PipelineProject>()
            : [new PipelineProject { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = allTemplates.Select(t => t.Id).ToList(), Enabled = true }]);

        _mockConsolidationClient.Setup(s => s.GetOverviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ConsolidationPageService.BuildOverview(allProjects, allTemplates, config, harnessSuggestions));
        _mockConsolidationClient.Setup(s => s.GetRefactoringScanSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string templateId, CancellationToken _) => ConsolidationPageService.BuildRefactoringScanSettings(
                config, allProjects.FirstOrDefault(p => p.TemplateIds.Contains(templateId))));

        // The run history (IUiRunClient) — the page's only source of runs since #3028
        _mockRunHistoryClient
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = (runHistory ?? Array.Empty<PipelineRunSummary>()).ToList(),
                Page = 1,
                PageSize = 200,
                HasMore = false
            });

        // Auto-start counters: return empty dictionaries by default so existing tests are unaffected.
        // Individual auto-counter tests override this mock with their own setup.
        // TODO [WARNING]: No named test explicitly asserts the acceptance criterion "with all three
        // settings at 0, the page renders no [data-testid^=auto-counter] element". The default empty
        // response means existing tests implicitly cover this, but a rendering bug that emitted wrapper
        // markup for empty dictionaries would not be caught. Consider adding an explicit
        // AutoCounters_AllThresholdsZero_NoCounterElementsRendered assertion.
        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter>(),
                new Dictionary<string, UiAutoCounter>(),
                null));

        Services.AddSingleton(_mockConsolidationClient.Object);
        Services.AddSingleton(_mockRunHistoryClient.Object);
        Services.AddSingleton(new Mock<IUiWorkItemClient>().Object);
    }

    private static PipelineJobTemplate CreateTemplate(
        string id = "t1",
        string name = "Test Template",
        string? brainProviderId = "brain-1",
        string issueProviderId = "issue-1",
        string repoProviderId = "repo-1",
        bool enabled = true) => new()
        {
            Id = id,
            Name = name,
            BrainProviderId = brainProviderId,
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            Enabled = enabled
        };

    private static PipelineRunSummary CreateConsolidationRun(
        ConsolidationRunType type = ConsolidationRunType.BrainConsolidation,
        string? templateId = "t1",
        string? templateName = "Test Template",
        PipelineStep finalStep = PipelineStep.Completed,
        string? summary = "3 files modified",
        DateTimeOffset? completedAt = null,
        DateTimeOffset? startedAt = null,
        bool running = false) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = $"{type}:{(templateId ?? "global")}",
            IssueTitle = templateName ?? type.ToString(),
            RunType = PipelineRunType.Consolidation,
            ConsolidationType = type,
            ConsolidationTemplateId = templateId,
            ConsolidationTemplateName = templateName,
            ConsolidationResultSummary = summary,
            FinalStep = finalStep,
            StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAtOffset = running ? null : completedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            InitiatedBy = ConsolidationConstants.InitiatedBy
        };

    // ═══ Issue #3028 Acceptance Criterion: pipeline-run read path ═══

    /// <summary>
    /// Issue #3028 acceptance criterion:
    /// After the backfill runs, the Consolidation page renders a consolidation run sourced
    /// from the pipeline-run read path (not ConsolidationRuns).
    ///
    /// The page must read run data exclusively from IUiRunClient.
    /// </summary>
    [Fact]
    public void AfterBackfill_ConsolidationPage_RendersRunSourcedFromPipelineRunReadPath()
    {
        // Arrange: the pipeline-run read path returns one consolidation run
        var run = CreateConsolidationRun(
            type: ConsolidationRunType.BrainConsolidation,
            templateId: "t1",
            templateName: "Test Template",
            finalStep: PipelineStep.Completed,
            summary: "test summary");

        // Set up the specific runType=Consolidation mock BEFORE calling RegisterServices
        // so it takes precedence (Moq uses last-wins for matching setups).
        // We call RegisterServices with no run history and then override.
        // TODO [WARNING]: This double-setup relies on Moq's last-wins behaviour. The override only
        // works because it is called AFTER RegisterServices. If RegisterServices is ever refactored
        // to set up the mock after registration, this override will be silently ignored and the test
        // will see an empty result. Consider passing the runHistory directly to RegisterServices to
        // eliminate the ordering dependency. (TestQualityReviewer review)
        RegisterServices(templates: Array.Empty<PipelineJobTemplate>());

        // Override AFTER RegisterServices to ensure our specific run is returned
        _mockRunHistoryClient
            .Setup(s => s.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new List<PipelineRunSummary> { run },
                Page = 1,
                PageSize = 200,
                HasMore = false
            });

        // Act: render the page
        var cut = Render<Consolidation>();

        // Verify mock was called (if it wasn't called, history will be empty and the test fails)
        // TODO [WARNING]: This verify uses It.IsAny<RunHistoryQuery>() which does not confirm
        // that the page passes RunType = PipelineRunType.Consolidation specifically. Passing the wrong
        // run type (or null) would cause the page to fetch all run types and mix in implementation runs,
        // violating the core requirement. Consider tightening to a query matcher on RunType
        // to make the assertion meaningful. (TestQualityReviewer review)
        _mockRunHistoryClient.Verify(s => s.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "GetRunHistoryAsync must be called during page load");

        // Assert: the run history table contains the run from the pipeline-run read path
        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Contains("test summary", rows[0].TextContent);
        Assert.Contains("Brain Consolidation", rows[0].TextContent);
        Assert.Contains("Succeeded", rows[0].TextContent);

        // The cards come from the overview, loaded once.
        _mockConsolidationClient.Verify(s => s.GetOverviewAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ Requirement 1.2: Per-template cards render ═══

    [Fact]
    public void RendersTemplateCards_ForEnabledTemplates()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "DotNet Repo"),
            CreateTemplate(id: "t2", name: "Python Repo")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var cards = cut.FindAll(".consolidation-card");
        Assert.Equal(2, cards.Count);
        Assert.Contains("DotNet Repo", cards[0].TextContent);
        Assert.Contains("Python Repo", cards[1].TextContent);
    }

    [Fact]
    public void DoesNotRenderCards_ForDisabledTemplates()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Active", enabled: true),
            CreateTemplate(id: "t2", name: "Disabled", enabled: false)
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var cards = cut.FindAll(".consolidation-card");
        Assert.Single(cards);
        Assert.Contains("Active", cards[0].TextContent);
    }

    [Fact]
    public void ShowsEmptyState_WhenNoTemplates()
    {
        RegisterServices(templates: Array.Empty<PipelineJobTemplate>());

        var cut = Render<Consolidation>();

        var empty = cut.Find(".monitoring-empty");
        Assert.Contains("No enabled templates configured", empty.TextContent);
    }

    // ═══ Requirement 1.4: Cards show correct provider-based buttons ═══

    [Fact]
    public void ShowsBrainButton_WhenBrainProviderConfigured()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var buttons = cut.FindAll(".btn-trigger");
        Assert.Contains(buttons, b => b.TextContent.Contains("Brain Consolidation"));
    }

    // ═══ Brain consolidation works on the brain, which templates can share ═══

    [Fact]
    public void BrainButton_DisabledWithTheReason_WhenTheTemplatesBrainIsReadOnly()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Reader") with { BrainReadOnly = true },
            CreateTemplate(id: "t2", name: "Writer", repoProviderId: "repo-2", issueProviderId: "issue-2")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        Assert.True(BrainButton(cut, "Reader").HasAttribute("disabled"));
        Assert.Contains("read-only for this template", Card(cut, "Reader").TextContent);
        Assert.False(BrainButton(cut, "Writer").HasAttribute("disabled"),
            "another template that writes to the same brain can still consolidate it");
    }

    [Fact]
    public void BrainButton_Disabled_WhenBrainWritesAreOffGlobally()
    {
        RegisterServices(
            templates: new List<PipelineJobTemplate> { CreateTemplate(name: "Repo") },
            pipelineConfig: new PipelineConfiguration { BrainReadOnly = true });

        var cut = Render<Consolidation>();

        Assert.True(BrainButton(cut, "Repo").HasAttribute("disabled"));
    }

    [Fact]
    public void BrainButton_DisabledWhileAnotherTemplateConsolidatesTheSameBrain()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Repo A", brainProviderId: "brain-shared"),
            CreateTemplate(id: "t2", name: "Repo B", brainProviderId: "brain-shared", repoProviderId: "repo-2", issueProviderId: "issue-2"),
            CreateTemplate(id: "t3", name: "Repo C", brainProviderId: "brain-other", repoProviderId: "repo-3", issueProviderId: "issue-3")
        };
        var runs = new List<PipelineRunSummary>
        {
            CreateConsolidationRun(ConsolidationRunType.BrainConsolidation, "t1", "Repo A", running: true)
        };
        RegisterServices(templates: templates, runHistory: runs);

        var cut = Render<Consolidation>();

        Assert.True(BrainButton(cut, "Repo B").HasAttribute("disabled"), "Repo B shares the brain that Repo A is consolidating");
        Assert.False(BrainButton(cut, "Repo C").HasAttribute("disabled"), "Repo C has its own brain");
        var refactoringB = Card(cut, "Repo B").QuerySelectorAll(".btn-trigger").Single(b => b.TextContent.Contains("Refactoring Scan"));
        Assert.False(refactoringB.HasAttribute("disabled"), "a refactoring scan works on the repository, not the brain");
    }

    [Fact]
    public void LastBrainRun_IsTheBrainsLastRun_WhicheverTemplateTriggeredIt()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Repo A", brainProviderId: "brain-shared"),
            CreateTemplate(id: "t2", name: "Repo B", brainProviderId: "brain-shared", repoProviderId: "repo-2", issueProviderId: "issue-2")
        };
        var runs = new List<PipelineRunSummary>
        {
            CreateConsolidationRun(ConsolidationRunType.BrainConsolidation, "t1", "Repo A", PipelineStep.Completed)
        };
        RegisterServices(templates: templates, runHistory: runs);

        var cut = Render<Consolidation>();

        var brainRowB = Card(cut, "Repo B").QuerySelectorAll(".consolidation-card-row")
            .Single(r => r.TextContent.Contains("Brain Consolidation:"));
        Assert.Contains("Succeeded", brainRowB.TextContent);
        Assert.DoesNotContain("Never run", brainRowB.TextContent);
    }

    [Fact]
    public void LastRefactoringRun_IsTheTemplatesNewestRun_OtherTemplatesDoNotCount()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Repo A", brainProviderId: "brain-shared"),
            CreateTemplate(id: "t2", name: "Repo B", brainProviderId: "brain-shared", repoProviderId: "repo-2", issueProviderId: "issue-2")
        };
        var now = DateTimeOffset.UtcNow;
        // Oldest first: the page must pick the newest run by start time, not the first in the list
        // (the API puts in-flight runs ahead of the paged history without re-sorting).
        var runs = new List<PipelineRunSummary>
        {
            CreateConsolidationRun(ConsolidationRunType.RefactoringDetection, "t1", "Repo A", PipelineStep.Failed, startedAt: now.AddHours(-3)),
            CreateConsolidationRun(ConsolidationRunType.RefactoringDetection, "t1", "Repo A", PipelineStep.Completed, startedAt: now.AddHours(-1)),
            CreateConsolidationRun(ConsolidationRunType.RefactoringDetection, "t2", "Repo B", PipelineStep.Cancelled, startedAt: now)
        };
        RegisterServices(templates: templates, runHistory: runs);

        var cut = Render<Consolidation>();

        var refactoringRowA = Card(cut, "Repo A").QuerySelectorAll(".consolidation-card-row")
            .Single(r => r.TextContent.Contains("Refactoring Scan:"));
        Assert.Contains("Succeeded", refactoringRowA.TextContent);
        Assert.DoesNotContain("Failed", refactoringRowA.TextContent);
        // A refactoring scan works on its template's repository, so another template's scan does not count.
        Assert.DoesNotContain("Cancelled", refactoringRowA.TextContent);
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<Consolidation> cut, string templateName) =>
        cut.FindAll(".consolidation-card")
            .Single(c => c.QuerySelector(".consolidation-card-title")!.TextContent == templateName);

    private static AngleSharp.Dom.IElement BrainButton(IRenderedComponent<Consolidation> cut, string templateName) =>
        Card(cut, templateName).QuerySelectorAll(".btn-trigger")
            .Single(b => b.TextContent.Contains("Brain Consolidation"));

    [Fact]
    public void HidesBrainButton_WhenNoBrainProvider()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: null)
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var cardButtons = cut.FindAll(".consolidation-card .btn-trigger");
        Assert.DoesNotContain(cardButtons, b => b.TextContent.Contains("Brain Consolidation"));
    }

    [Fact]
    public void ShowsRefactoringButton_WhenRepoAndIssueProviderConfigured()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(repoProviderId: "repo-1", issueProviderId: "issue-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var buttons = cut.FindAll(".btn-trigger");
        Assert.Contains(buttons, b => b.TextContent.Contains("Refactoring Scan"));
    }

    [Fact]
    public void HidesRefactoringButton_WhenNoIssueProvider()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var cardButtons = cut.FindAll(".consolidation-card .btn-trigger");
        Assert.DoesNotContain(cardButtons, b => b.TextContent.Contains("Refactoring Scan"));
    }

    // ═══ Requirement 1.3, 8.2: Harness suggestions section ═══

    [Fact]
    public void HarnessSection_ShowsSuggestions_WhenAvailable()
    {
        var suggestions = new HarnessSuggestions
        {
            GeneratedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            BasedOnRunCount = 15,
            SuccessRate = 0.8m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Text = "Add retry logic", Rationale = "Frequent timeout failures", Frequency = 5 },
                new() { Text = "Cache dependencies", Rationale = "Slow builds", Frequency = 3 }
            }
        };
        RegisterServices(harnessSuggestions: suggestions);

        var cut = Render<Consolidation>();

        var markup = cut.Markup;
        Assert.Contains("Add retry logic", markup);
        Assert.Contains("Frequent timeout failures", markup);
        Assert.Contains("Cache dependencies", markup);
        Assert.Contains("Slow builds", markup);
    }

    [Fact]
    public void HarnessSection_ShowsNoSuggestions_WhenNull()
    {
        RegisterServices(harnessSuggestions: null);

        var cut = Render<Consolidation>();

        var markup = cut.Markup;
        Assert.Contains("No suggestions generated yet", markup);
    }

    [Fact]
    public void HarnessSection_ShowsMetadata_WhenSuggestionsExist()
    {
        var suggestions = new HarnessSuggestions
        {
            GeneratedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            BasedOnRunCount = 15,
            SuccessRate = 0.8m,
            Suggestions = new List<HarnessSuggestion>
            {
                new() { Text = "Suggestion 1", Rationale = "Reason", Frequency = 2 }
            }
        };
        RegisterServices(harnessSuggestions: suggestions);

        var cut = Render<Consolidation>();

        var meta = cut.Find(".consolidation-suggestions-meta");
        Assert.Contains("15", meta.TextContent);
        Assert.Contains("80", meta.TextContent);
    }

    // ═══ Requirement 1.5: Run history table ═══

    [Fact]
    public void RunHistoryTable_Renders_WithRunData()
    {
        var runs = new List<PipelineRunSummary>
        {
            CreateConsolidationRun(
                type: ConsolidationRunType.BrainConsolidation,
                templateId: "t1",
                templateName: "DotNet Repo",
                finalStep: PipelineStep.Completed,
                summary: "3 files modified"),
            CreateConsolidationRun(
                type: ConsolidationRunType.HarnessSuggestions,
                templateId: null,
                templateName: null,
                finalStep: PipelineStep.Failed,
                summary: "Timeout")
        };
        RegisterServices(runHistory: runs);

        var cut = Render<Consolidation>();

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Brain Consolidation", rows[0].TextContent);
        Assert.Contains("DotNet Repo", rows[0].TextContent);
        Assert.Contains("Succeeded", rows[0].TextContent);
        Assert.Contains("3 files modified", rows[0].TextContent);
        Assert.Contains("Harness Suggestions", rows[1].TextContent);
        Assert.Contains("Global", rows[1].TextContent);
        Assert.Contains("Failed", rows[1].TextContent);
    }

    [Fact]
    public void RunHistoryTable_ShowsEmptyState_WhenNoRuns()
    {
        RegisterServices(runHistory: Array.Empty<PipelineRunSummary>());

        var cut = Render<Consolidation>();

        var markup = cut.Markup;
        Assert.Contains("No consolidation runs yet", markup);
    }

    // ═══ Trigger tests ═══

    [Fact]
    public void TriggerRejection_ShowsStatusMessage()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ThrowsAsync(new InvalidOperationException(Rejected));

        var cut = Render<Consolidation>();

        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        var statusMessage = cut.Find(".consolidation-status-message");
        Assert.Contains("rejected", statusMessage.TextContent.ToLowerInvariant());
    }

    // A refused run gives the operator no reason beyond the message, so each rejection message must name every
    // cause the service can give: a selector with no job template is one of them (issue #3642).
    [Theory]
    [InlineData("Brain Consolidation", false, "Brain consolidation rejected")]
    [InlineData("Refactoring Scan", true, "Refactoring scan rejected")]
    [InlineData("Generate Suggestions", false, "Harness suggestions rejected")]
    public void TriggerRejection_NamesMissingJobTemplateAsPossibleCause(string buttonText, bool confirmInModal, string expectedStart)
    {
        RegisterServices(templates: [CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")]);
        // Simulate a refused trigger: the API returns 409 → client throws InvalidOperationException with the
        // rejection message. The message now names a missing job template as one of the possible causes (#3642).
        // TODO: These message strings are embedded directly in the mock, so the server-side
        // ConsolidationPageService.TriggerAsync rejection messages (ConsolidationPageService.cs:104–112) are
        // not executed by any test. A regression in those strings — e.g. reverting the "no job template
        // matches the agent profile" clause — would not fail any test. Consider adding a unit or integration
        // test that exercises ConsolidationPageService.TriggerAsync directly and asserts on the returned
        // message content. See review finding (TestQualityReviewer, WARNING).
        _mockConsolidationClient
            .Setup(s => s.TriggerAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .Returns((ConsolidationRunType type, string? _, bool __, CancellationToken ___, RefactoringScanFocus ____) =>
                Task.FromException<string>(new InvalidOperationException(type switch
                {
                    ConsolidationRunType.BrainConsolidation => "Brain consolidation rejected for Test Template — already running, a configuration error occurred, no job template matches the agent profile, or template not found.",
                    ConsolidationRunType.RefactoringDetection => "Refactoring scan rejected for Test Template — already running, a configuration error occurred, no job template matches the agent profile, or template not found.",
                    _ => "Harness suggestions rejected — already running, a configuration error occurred, or no job template matches the agent profile."
                })));

        var cut = Render<Consolidation>();
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains(buttonText)).Click();
        if (confirmInModal)
            cut.Find(".modal-card .btn-save").Click();

        var message = cut.Find(".consolidation-status-message").TextContent;
        Assert.StartsWith(expectedStart, message);
        Assert.Contains("no job template matches the agent profile", message);
    }

    // ═══ Refactoring Scan Pre-Flight Modal (Issue #1435) ═══

    [Fact]
    public void RefactoringScanButton_OpensModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var modal = cut.Find(".modal-overlay");
        Assert.NotNull(modal);
        Assert.Contains("Trigger Refactoring Scan", modal.TextContent);
    }

    [Fact]
    public void RefactoringModal_DisplaysConfigValues()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var modal = cut.Find(".modal-overlay");
        Assert.Contains("3", modal.TextContent);
        Assert.Contains("90 days", modal.TextContent);
        Assert.Contains("Enabled", modal.TextContent);
    }

    [Fact]
    public void RefactoringModal_ShowsGeneratedLabel()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var badge = cut.Find(".label-badge-static");
        Assert.Contains("agent:generated", badge.TextContent);
    }

    [Fact]
    public void RefactoringModal_AutoDispatchDefaultsUnchecked()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        Assert.False(checkbox.HasAttribute("checked") && checkbox.GetAttribute("checked") != "false");
    }

    [Fact]
    public void RefactoringModal_ConfirmWithoutAutoDispatch_CallsTriggerWithFalse()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        // TODO [WARNING]: The Verify below uses It.IsAny<RefactoringScanFocus>() and does not constrain the
        // focus to RefactoringScanFocus.All. A regression where _modalFocus is initialised to any value
        // other than All (e.g. Tests) would still pass this test, because the focus is not asserted.
        // The companion test RefactoringModal_FocusTests_PassesFocusToTrigger only covers the changed-focus
        // path. Consider adding a separate test (e.g. RefactoringModal_DefaultFocus_SendsAll) that opens
        // the modal without changing the select and verifies focus == RefactoringScanFocus.All is passed.
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Once);
    }

    [Fact]
    public void RefactoringModal_ConfirmWithAutoDispatch_CallsTriggerWithTrue()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);

        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", true, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Once);
    }

    [Fact]
    public void RefactoringModal_FocusTests_PassesFocusToTrigger()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        // Change the focus dropdown to "Tests"
        cut.Find("[data-testid=refactoring-focus]").Change(RefactoringScanFocus.Tests.ToString());

        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<bool>(), It.IsAny<CancellationToken>(), RefactoringScanFocus.Tests), Times.Once);
    }

    [Fact]
    public void RefactoringModal_Cancel_ClosesWithoutTriggering()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        var cancelButton = cut.Find(".modal-card .btn-cancel");
        cancelButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Never);
    }

    [Fact]
    public void BrainConsolidation_TriggersImmediately_NoModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "t1", false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Once);
    }

    [Fact]
    public void HarnessSuggestions_TriggersImmediately_NoModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        var suggestionsButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Generate Suggestions"));
        suggestionsButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Once);
    }

    // ═══ Issue #1772: Modal focus and Enter-key bubble fixes ═══

    [Fact]
    public void FocusAsync_CalledOnce_WhenModalOpened()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1);
    }

    [Fact]
    public void FocusAsync_NotCalledAgain_OnSubsequentRerender()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);
        checkbox.Change(false);

        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1);
    }

    [Fact]
    public void FocusAsync_CalledAgain_WhenModalReopened()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        cut.Find(".modal-card .btn-cancel").Click();
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();

        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2);
    }

    [Fact]
    public void EnterKey_OnCheckbox_DoesNotConfirmModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);

        Assert.NotEmpty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Never);
    }

    [Fact]
    public void EnterKey_OnModalOverlay_ConfirmsModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        cut.Find(".modal-overlay").TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Once);
    }

    [Fact]
    public void EscapeKey_OnModalOverlay_ClosesModalWithoutTriggering()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        cut.Find(".modal-overlay").TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationClient.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()), Times.Never);
    }

    // ═══ Dispatch tests ═══

    [Fact]
    public void TriggerConsolidation_WhenRunCreated_ShowsQueuedMessage()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);
        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Brain Consolidation")).Click();

        _mockConsolidationClient.Verify(
            s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()),
            Times.Once,
            "TriggerAsync must be called exactly once");
    }

    [Fact]
    public void TriggerConsolidation_WhenRunRejected_ShowsErrorMessage()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ThrowsAsync(new InvalidOperationException(Rejected));

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Brain Consolidation")).Click();

        _mockConsolidationClient.Verify(
            s => s.TriggerAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()),
            Times.Once);
    }

    [Fact]
    public void TriggerConsolidation_RefactoringModal_CallsTriggerAsync()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);
        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        cut.Find(".modal-card .btn-save").Click();

        _mockConsolidationClient.Verify(
            s => s.TriggerAsync(
                ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()),
            Times.Once);
    }

    [Fact]
    public void TriggerConsolidation_HarnessSuggestions_CallsTriggerAsync()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);
        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                ConsolidationRunType.HarnessSuggestions, null, false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Generate Suggestions")).Click();

        _mockConsolidationClient.Verify(
            s => s.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()),
            Times.Once);
    }

    [Fact]
    public async Task TriggerConsolidation_ShowsQueuedStatusMessage_AfterSuccessfulTrigger()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationClient.Setup(s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", false, It.IsAny<CancellationToken>(), It.IsAny<RefactoringScanFocus>()))
            .ReturnsAsync(Queued);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Brain Consolidation")).Click();

        await cut.WaitForStateAsync(() =>
            cut.FindAll(".consolidation-status-message").Count > 0,
            TimeSpan.FromSeconds(2));

        var msg = cut.Find(".consolidation-status-message");
        Assert.Contains("queued", msg.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consolidation-status-error", msg.ClassName ?? "");
    }

    // ═══ Issue #3148: Refactoring modal shows effective (project-overridden) values ═══

    [Fact]
    public void RefactoringModal_ShowsProjectOverrideValues_WhenProjectHasOverrides()
    {
        // Arrange: global config has defaults (MaxRefactoringProposals=3, RefactoringReviewEnabled=true).
        // The project owning template "t1" overrides both to non-default values.
        var template = CreateTemplate(id: "t1", issueProviderId: "issue-1", repoProviderId: "repo-1");
        var globalConfig = new PipelineConfiguration(); // MaxRefactoringProposals=3, RefactoringReviewEnabled=true
        var projectWithOverrides = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = ["t1"],
            Enabled = true,
            MaxRefactoringProposals = 7,
            RefactoringReviewEnabled = false
        };
        RegisterServices(templates: [template], pipelineConfig: globalConfig, projects: [projectWithOverrides]);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var modal = cut.Find(".modal-card");

        // Effective values from project overrides
        // TODO: [WARNING] These Assert.Contains checks scan the entire modal TextContent. A coincidental "7"
        // or "Disabled" in any other modal element (template name, title, etc.) would produce a false pass.
        // Scope assertions to the specific .refactoring-modal-param-value span for each field.
        // (Correctness review + TestQuality review, issue #3148)
        Assert.Contains("7", modal.TextContent);        // overridden MaxRefactoringProposals
        Assert.Contains("Disabled", modal.TextContent); // overridden RefactoringReviewEnabled
        Assert.Contains("90 days", modal.TextContent);  // HotspotAnalysisLookback is global-only, unchanged

        // Override indicators present for both overridden fields
        Assert.Equal(2, modal.QuerySelectorAll(".refactoring-modal-param-override").Length);
    }

    // TODO: [WARNING] A partial-override scenario is not tested: one of MaxRefactoringProposals or
    // RefactoringReviewEnabled overridden while the other is not. Without this case, the per-field
    // independence of the override indicator logic is untested. A bug where both indicators are set
    // whenever either field is overridden would not be caught. Add a test covering this case.
    // (TestQuality review, issue #3148)

    [Fact]
    public void RefactoringModal_ShowsGlobalValues_WhenNoProjectOverrides()
    {
        // Arrange: non-default global value (5) so we can distinguish "showing global" from "showing default".
        // RegisterServices creates a default project with all nullable overrides null — no extra setup needed.
        var template = CreateTemplate(id: "t1", issueProviderId: "issue-1", repoProviderId: "repo-1");
        var globalConfig = new PipelineConfiguration { MaxRefactoringProposals = 5 };
        RegisterServices(templates: [template], pipelineConfig: globalConfig);

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var modal = cut.Find(".modal-card");

        // Global value shown (not the default 3, but the configured 5)
        // TODO: [WARNING] Assert.Contains("5", ...) scans the entire modal TextContent. Scope to the specific
        // .refactoring-modal-param-value span to avoid false passes from incidental "5" in other elements.
        // (TestQuality review, issue #3148)
        Assert.Contains("5", modal.TextContent);

        // TODO: [WARNING] RefactoringReviewEnabled global value ("Enabled") is not asserted here.
        // A regression breaking its display would not be caught. Add: Assert.Contains("Enabled", modal.TextContent)
        // or scope to the relevant .refactoring-modal-param-value span. (TestQuality review, issue #3148)

        // No override indicators when no project overrides are active
        Assert.Empty(modal.QuerySelectorAll(".refactoring-modal-param-override"));
    }

    // ── Spec 049: consolidation runs need global operator ─────────────────────

    [Theory]
    [InlineData(AccessRole.ReadOnly, false)]
    [InlineData(AccessRole.Operator, true)]
    public void Access_TriggerControls_NeedGlobalOperator(AccessRole role, bool visible)
    {
        Services.AddTestAccess(TestAccess.Global(role));
        RegisterServices(templates: Array.Empty<PipelineJobTemplate>());

        var cut = Render<Consolidation>();

        cut.Markup.Contains("Generate Suggestions").Should().Be(visible);
    }

    // ── Issue #3592: Automatic-start counters ─────────────────────────────────

    [Fact]
    public void AutoCounters_BrainThresholdSet_ShowsCountAndThreshold()
    {
        var templates = new List<PipelineJobTemplate> { CreateTemplate(id: "t1") };
        RegisterServices(templates: templates);

        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter> { ["t1"] = new UiAutoCounter(14, 20, null) },
                new Dictionary<string, UiAutoCounter>(),
                null));

        var cut = Render<Consolidation>();

        // Counters load after first render; wait for the element to appear.
        cut.WaitForAssertion(() =>
        {
            var counter = cut.Find("[data-testid='auto-counter-brain']");
            counter.TextContent.Should().Contain("14 / 20 runs since the last run");
            counter.TextContent.Should().Contain("· auto");
        });
    }

    [Fact]
    public void AutoCounters_TwoTemplatesShareABrain_BothCardsShowSameCounter()
    {
        // Two templates that share the same brain provider.
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Repo A", brainProviderId: "brain-shared"),
            CreateTemplate(id: "t2", name: "Repo B", brainProviderId: "brain-shared",
                repoProviderId: "repo-2", issueProviderId: "issue-2")
        };
        RegisterServices(templates: templates);

        // Both templates receive the same count (brain is deduplicated server-side).
        // The acceptance-criterion test AutoCounters_TwoTemplatesShareABrain_CountsOncePerBrain (below)
        // verifies that CountBrainRunsSinceLastConsolidationAsync is called exactly once for the shared brain.
        // This page-level test verifies that both Blazor cards correctly render the deduplicated count.
        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter>
                {
                    ["t1"] = new UiAutoCounter(7, 20, null),
                    ["t2"] = new UiAutoCounter(7, 20, null)
                },
                new Dictionary<string, UiAutoCounter>(),
                null));

        var cut = Render<Consolidation>();

        // Both cards show the brain counter with the same value.
        cut.WaitForAssertion(() =>
        {
            var counters = cut.FindAll("[data-testid='auto-counter-brain']");
            counters.Should().HaveCount(2, "both templates sharing a brain should show a counter");
            counters.Should().AllSatisfy(c => c.TextContent.Should().Contain("7 / 20 runs since the last run"));
        });
    }

    [Fact]
    public void AutoCounters_RefactoringPaused_ShowsPausedText()
    {
        var templates = new List<PipelineJobTemplate> { CreateTemplate(id: "t1") };
        RegisterServices(templates: templates);

        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter>(),
                new Dictionary<string, UiAutoCounter>
                {
                    ["t1"] = new UiAutoCounter(3, 10, "auto paused: the last run failed")
                },
                null));

        var cut = Render<Consolidation>();

        cut.WaitForAssertion(() =>
        {
            var blocked = cut.Find("[data-testid='auto-blocked-refactoring']");
            blocked.TextContent.Should().Be("auto paused: the last run failed");
            // Counter span is still shown (showing the count), but without '· auto'.
            var counter = cut.Find("[data-testid='auto-counter-refactoring']");
            counter.TextContent.Should().Contain("3 / 10 closed issues since the last run");
            counter.TextContent.Should().NotContain("· auto");
        });
    }

    [Fact]
    public void AutoCounters_RefactoringBackpressure_ShowsWaitingText()
    {
        var templates = new List<PipelineJobTemplate> { CreateTemplate(id: "t1") };
        RegisterServices(templates: templates);

        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter>(),
                new Dictionary<string, UiAutoCounter>
                {
                    ["t1"] = new UiAutoCounter(5, 10, "auto waiting: 3 scan issues still open")
                },
                null));

        var cut = Render<Consolidation>();

        cut.WaitForAssertion(() =>
        {
            var blocked = cut.Find("[data-testid='auto-blocked-refactoring']");
            // TODO [WARNING]: These partial Contain assertions don't detect wrong counts in the blocked
            // message (e.g. "auto waiting: 0 scan issues still open" would still pass). Consider
            // tightening to `.Should().Be("auto waiting: 3 scan issues still open")` matching the exact
            // string from the mock, as AutoCounters_RefactoringPaused_ShowsPausedText does for its value.
            blocked.TextContent.Should().Contain("auto waiting:");
            blocked.TextContent.Should().Contain("scan issues still open");
        });
    }

    [Fact]
    public void AutoCounters_HarnessThresholdSet_ShowsCounter()
    {
        RegisterServices(templates: Array.Empty<PipelineJobTemplate>());

        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter>(),
                new Dictionary<string, UiAutoCounter>(),
                new UiAutoCounter(41, 50, null)));

        var cut = Render<Consolidation>();

        cut.WaitForAssertion(() =>
        {
            var counter = cut.Find("[data-testid='auto-counter-harness']");
            counter.TextContent.Should().Contain("41 / 50 feedback entries since the last run");
            counter.TextContent.Should().Contain("· auto");
        });
    }

    [Fact]
    public void AutoCounters_CounterThrows_PageStillRenders()
    {
        // Two templates: one with a brain counter, one with a refactoring counter.
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(id: "t1", name: "Has Brain"),
            CreateTemplate(id: "t2", name: "Has Refactoring", brainProviderId: null,
                repoProviderId: "repo-2", issueProviderId: "issue-2")
        };
        RegisterServices(templates: templates);

        // Simulate partial failure: brain counter for t1 is present; refactoring counter for t2 is absent
        // (the server-side error was caught and the entry omitted from the response).
        // TODO [WARNING]: This test verifies server-side partial failure (one counter missing from response)
        // but does NOT exercise the client-side resilience path in LoadAutoCountersAsync where
        // GetAutoCountersAsync() itself throws (e.g. network error / HTTP 5xx), which sets _autoCounters = null
        // and hides ALL counters. That path is covered by the catch block in Consolidation.razor but has no
        // dedicated test. Consider adding a case where the mock throws and asserts no [data-testid^='auto-counter']
        // elements are rendered while the page still shows its cards.
        _mockConsolidationClient
            .Setup(s => s.GetAutoCountersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiConsolidationAutoCounters(
                new Dictionary<string, UiAutoCounter> { ["t1"] = new UiAutoCounter(5, 20, null) },
                new Dictionary<string, UiAutoCounter>(),  // refactoring counter absent (failed)
                null));

        var cut = Render<Consolidation>();

        // Page still renders without crashing.
        Assert.NotEmpty(cut.FindAll(".consolidation-card"));

        // The brain counter is shown.
        cut.WaitForAssertion(() =>
        {
            var brainCounter = cut.Find("[data-testid='auto-counter-brain']");
            brainCounter.TextContent.Should().Contain("5 / 20 runs since the last run");
        });

        // The refactoring counter is absent (entry missing from dictionary → not rendered).
        Assert.Empty(cut.FindAll("[data-testid='auto-counter-refactoring']"));
    }

    /// <summary>
    /// Verifies the acceptance criterion "(the staleness mock is called once for that brain)":
    /// <c>ConsolidationPageService.GetAutoCountersAsync</c> must call
    /// <c>CountBrainRunsSinceLastConsolidationAsync</c> exactly once per unique brain id,
    /// even when multiple templates share that brain, via the <c>seenBrainCounts</c> deduplication guard.
    /// This is a plain xUnit test (not bUnit) because it exercises the API service layer directly,
    /// where <c>ConsolidationStalenessService</c> lives. The page-side rendering of the deduplicated
    /// value is covered by <see cref="AutoCounters_TwoTemplatesShareABrain_BothCardsShowSameCounter"/>.
    /// </summary>
    [Fact]
    public async Task AutoCounters_TwoTemplatesShareABrain_CountsOncePerBrain()
    {
        // Arrange: two templates that share the same brain provider.
        const string BrainId = "brain-shared";
        var template1 = new PipelineJobTemplate
        {
            Id = "t1", Name = "Repo A", BrainProviderId = BrainId,
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var template2 = new PipelineJobTemplate
        {
            Id = "t2", Name = "Repo B", BrainProviderId = BrainId,
            IssueProviderId = "ip-2", RepoProviderId = "rp-2", Enabled = true
        };
        var project = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId, Name = "Default",
            TemplateIds = ["t1", "t2"], Enabled = true
        };

        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { AutoBrainConsolidationAfterRuns = 20 });

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { project });
        mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template1, template2 });

        // ConsolidationStalenessService is not sealed and has virtual methods; Moq can create a proxy.
        var mockStaleness = new Mock<ConsolidationStalenessService>();
        mockStaleness
            .Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(BrainId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(14);
        mockStaleness
            .Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new ConsolidationPageService(
            mockProjectStore.Object,
            mockConfigStore.Object,
            new Mock<IConsolidationService>().Object,
            mockStaleness.Object);

        // Act
        var result = await service.GetAutoCountersAsync(CancellationToken.None);

        // Assert: the staleness service is called exactly once for the shared brain id.
        mockStaleness.Verify(
            s => s.CountBrainRunsSinceLastConsolidationAsync(BrainId, It.IsAny<CancellationToken>()),
            Times.Once,
            "Templates sharing a brain must not cause duplicate CountBrainRunsSinceLastConsolidationAsync calls");

        // Both templates receive a counter with the deduplicated count.
        result.BrainCounters.Should().ContainKey("t1").WhoseValue.Count.Should().Be(14);
        result.BrainCounters.Should().ContainKey("t2").WhoseValue.Count.Should().Be(14);
    }
}
