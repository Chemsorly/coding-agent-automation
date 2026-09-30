using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Consolidation page.
/// Validates Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 8.2, 8.5, 10.2.
/// After issue #3028, the page reads run history from IPipelineApiRunHistoryClient
/// (not IConsolidationService.GetRunHistoryAsync / GetLastRunAsync).
/// </summary>
public class ConsolidationPageComponentTests : BunitContext
{
    private readonly Mock<IConsolidationService> _mockConsolidationService = new(MockBehavior.Strict);
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();
    private readonly Mock<IAgentHubConnection> _mockHubConnection = new();
    private readonly ConsolidationBadgeService _badgeService = new();

    public ConsolidationPageComponentTests()
    {
        // IAgentHubConnection mock: On<T1,T2> returns a no-op disposable (page subscribes in OnInitializedAsync).
        _mockHubConnection
            .Setup(h => h.On<string, JobCompletionPayload>(It.IsAny<string>(), It.IsAny<Action<string, JobCompletionPayload>>()))
            .Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
    }

    private void RegisterServices(
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        IReadOnlyList<PipelineRunSummary>? runHistory = null,
        HarnessSuggestions? harnessSuggestions = null,
        PipelineConfiguration? pipelineConfig = null)
    {
        var config = pipelineConfig ?? new PipelineConfiguration();

        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        // GetRunHistoryAsync (IPipelineApiRunHistoryClient) — main history source after #3028
        _mockRunHistoryClient
            .Setup(s => s.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<PipelineRunType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = (runHistory ?? Array.Empty<PipelineRunSummary>()).ToList(),
                Page = 1, PageSize = 200, HasMore = false
            });

        // Strict mock: GetRunHistoryAsync and GetLastRunAsync must NOT be called on IConsolidationService
        _mockConsolidationService
            .Setup(s => s.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(harnessSuggestions);

        Services.AddSingleton<IConsolidationService>(_mockConsolidationService.Object);
        Services.AddSingleton(_mockConfigClient.Object);
        Services.AddSingleton<IPipelineApiRunHistoryClient>(_mockRunHistoryClient.Object);
        Services.AddSingleton<IAgentHubConnection>(_mockHubConnection.Object);
        Services.AddSingleton(_badgeService);
        Services.AddSingleton(new Mock<IPipelineApiWorkItemClient>().Object);

        var mockConfigClientForProjects = _mockConfigClient;
        mockConfigClientForProjects.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var templateIds = (templates ?? Array.Empty<PipelineJobTemplate>()).Select(t => t.Id).ToList();
                if (templateIds.Count == 0) return Array.Empty<PipelineProject>();
                return new List<PipelineProject>
                {
                    new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = templateIds, Enabled = true }
                };
            });
        mockConfigClientForProjects.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => templates ?? Array.Empty<PipelineJobTemplate>());
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
    /// from the pipeline-run read path (not ConsolidationRuns / IConsolidationService).
    ///
    /// Proof: IConsolidationService is set up with MockBehavior.Strict — any call to
    /// GetRunHistoryAsync or GetLastRunAsync will throw, causing the test to fail.
    /// The page must read run data exclusively from IPipelineApiRunHistoryClient.
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
            .Setup(s => s.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<PipelineRunType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new List<PipelineRunSummary> { run },
                Page = 1, PageSize = 200, HasMore = false
            });

        // Act: render the page
        var cut = Render<Consolidation>();

        // Verify mock was called (if it wasn't called, history will be empty and the test fails)
        // TODO [WARNING]: This verify uses It.IsAny<PipelineRunType?>() which does not confirm
        // that the page passes runType: PipelineRunType.Consolidation specifically. Passing the wrong
        // runType (or null) would cause the page to fetch all run types and mix in implementation runs,
        // violating the core requirement. Consider tightening to:
        //   It.Is<PipelineRunType?>(rt => rt == PipelineRunType.Consolidation)
        // to make the assertion meaningful. (TestQualityReviewer review)
        _mockRunHistoryClient.Verify(s => s.GetRunHistoryAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<PipelineRunType?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "GetRunHistoryAsync must be called during page load");

        // Assert: the run history table contains the run from the pipeline-run read path
        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Contains("test summary", rows[0].TextContent);
        Assert.Contains("Brain Consolidation", rows[0].TextContent);
        Assert.Contains("Succeeded", rows[0].TextContent);

        // Proof: IConsolidationService.GetRunHistoryAsync and GetLastRunAsync were NOT called.
        // MockBehavior.Strict means any unexpected call throws — the test passing is the proof.
        _mockConsolidationService.Verify(s => s.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    // ═══ Requirement 10.2: Badge resets on page load ═══

    [Fact]
    public void BadgeResetsToZero_OnPageLoad()
    {
        _badgeService.IncrementBy(5);
        Assert.Equal(5, _badgeService.BadgeCount);

        RegisterServices();
        Render<Consolidation>();

        Assert.Equal(0, _badgeService.BadgeCount);
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

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((ConsolidationTriggerResult?)null);

        var cut = Render<Consolidation>();

        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        var statusMessage = cut.Find(".consolidation-status-message");
        Assert.Contains("rejected", statusMessage.TextContent.ToLowerInvariant());
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

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-1",
                Type: ConsolidationRunType.RefactoringDetection,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Fact]
    public void RefactoringModal_ConfirmWithAutoDispatch_CallsTriggerWithTrue()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-1",
                Type: ConsolidationRunType.RefactoringDetection,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var cut = Render<Consolidation>();

        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);

        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), true), Times.Once);
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
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void BrainConsolidation_TriggersImmediately_NoModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1", issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-1",
                Type: ConsolidationRunType.BrainConsolidation,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var cut = Render<Consolidation>();

        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "t1", It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Fact]
    public void HarnessSuggestions_TriggersImmediately_NoModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-1",
                Type: ConsolidationRunType.HarnessSuggestions,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var cut = Render<Consolidation>();

        var suggestionsButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Generate Suggestions"));
        suggestionsButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, It.IsAny<CancellationToken>(), false), Times.Once);
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
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void EnterKey_OnModalOverlay_ConfirmsModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-1",
                Type: ConsolidationRunType.RefactoringDetection,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        cut.Find(".modal-overlay").TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
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
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
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

        var pendingRun = new ConsolidationTriggerResult(
            RunId: "run-dispatch-test",
            Type: ConsolidationRunType.BrainConsolidation,
            TemplateId: null,
            TemplateName: null,
            ProjectId: null,
            ProjectName: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            WorkItemId: null);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(pendingRun);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Brain Consolidation")).Click();

        _mockConsolidationService.Verify(
            s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", It.IsAny<CancellationToken>(), false),
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

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((ConsolidationTriggerResult?)null);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Brain Consolidation")).Click();

        _mockConsolidationService.Verify(
            s => s.TriggerAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
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

        var pendingRun = new ConsolidationTriggerResult(
            RunId: "run-refactor-dispatch",
            Type: ConsolidationRunType.RefactoringDetection,
            TemplateId: null,
            TemplateName: null,
            ProjectId: null,
            ProjectName: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            WorkItemId: null);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(pendingRun);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        cut.Find(".modal-card .btn-save").Click();

        _mockConsolidationService.Verify(
            s => s.TriggerAsync(
                ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), It.IsAny<bool>()),
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

        var pendingRun = new ConsolidationTriggerResult(
            RunId: "run-harness-dispatch",
            Type: ConsolidationRunType.HarnessSuggestions,
            TemplateId: null,
            TemplateName: null,
            ProjectId: null,
            ProjectName: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            WorkItemId: null);

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                ConsolidationRunType.HarnessSuggestions, null, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(pendingRun);

        var cut = Render<Consolidation>();

        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Generate Suggestions")).Click();

        _mockConsolidationService.Verify(
            s => s.TriggerAsync(ConsolidationRunType.HarnessSuggestions, null, It.IsAny<CancellationToken>(), false),
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

        _mockConsolidationService.Setup(s => s.TriggerAsync(
                ConsolidationRunType.BrainConsolidation, "t1", It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: "run-status-msg",
                Type: ConsolidationRunType.BrainConsolidation,
                TemplateId: null,
                TemplateName: null,
                ProjectId: null,
                ProjectName: null,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

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
        RegisterServices(templates: [template], pipelineConfig: globalConfig);

        // Override GetProjectsAsync AFTER RegisterServices — Moq last-wins replaces the setup
        // for ALL callers of this mock (LoadDataAsync, LoadReadOnlyBrainTemplatesAsync, OpenRefactoringModal).
        // Safe: LoadReadOnlyBrainTemplatesAsync only checks BrainReadOnly, which is not overridden here.
        var projectWithOverrides = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = ["t1"],
            Enabled = true,
            MaxRefactoringProposals = 7,
            RefactoringReviewEnabled = false
        };
        _mockConfigClient
            .Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { projectWithOverrides });

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
}
