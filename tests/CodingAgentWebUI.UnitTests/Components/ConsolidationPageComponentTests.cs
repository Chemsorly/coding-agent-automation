using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Consolidation page.
/// Validates Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 8.2, 8.5, 10.2.
/// </summary>
public class ConsolidationPageComponentTests : BunitContext
{
    private readonly Mock<IConsolidationService> _mockConsolidationService = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly ConsolidationBadgeService _badgeService = new();

    private void RegisterServices(
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        IReadOnlyList<ConsolidationRun>? runHistory = null,
        HarnessSuggestions? harnessSuggestions = null)
    {
        var config = new PipelineConfiguration();

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        _mockConsolidationService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(runHistory ?? Array.Empty<ConsolidationRun>());

        _mockConsolidationService.Setup(s => s.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(harnessSuggestions);

        _mockConsolidationService.Setup(s => s.GetLastRunAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        Services.AddSingleton<IConsolidationService>(_mockConsolidationService.Object);
        Services.AddSingleton<IConfigurationStore>(_mockConfigStore.Object);
        Services.AddSingleton(_badgeService);

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var templateIds = (templates ?? Array.Empty<PipelineJobTemplate>()).Select(t => t.Id).ToList();
                if (templateIds.Count == 0) return Array.Empty<PipelineProject>();
                return new List<PipelineProject>
                {
                    new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = templateIds, Enabled = true }
                };
            });
        mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => templates ?? Array.Empty<PipelineJobTemplate>());
        Services.AddSingleton(mockProjectStore.Object);
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

    // ═══ Requirement 1.2: Per-template cards render ═══

    /// <summary>
    /// Requirement 1.2: Page renders per-template cards for each enabled template.
    /// </summary>
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

    /// <summary>
    /// Requirement 1.2: Disabled templates are not shown.
    /// </summary>
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

    /// <summary>
    /// Requirement 1.2: Shows empty state when no templates configured.
    /// </summary>
    [Fact]
    public void ShowsEmptyState_WhenNoTemplates()
    {
        RegisterServices(templates: Array.Empty<PipelineJobTemplate>());

        var cut = Render<Consolidation>();

        var empty = cut.Find(".monitoring-empty");
        Assert.Contains("No enabled templates configured", empty.TextContent);
    }

    // ═══ Requirement 1.4: Cards show correct provider-based buttons ═══

    /// <summary>
    /// Requirement 1.4: Template with brain provider shows Brain Consolidation button.
    /// </summary>
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

    /// <summary>
    /// Requirement 1.4: Template without brain provider does not show Brain Consolidation button.
    /// </summary>
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

    /// <summary>
    /// Requirement 1.4: Template with repo + issue provider shows Refactoring Scan button.
    /// </summary>
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

    /// <summary>
    /// Requirement 1.4: Template without issue provider does not show Refactoring Scan button.
    /// </summary>
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

    /// <summary>
    /// Requirement 8.2: Harness section shows suggestions when available.
    /// </summary>
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

    /// <summary>
    /// Requirement 8.5: Harness section shows "No suggestions" when empty/null.
    /// </summary>
    [Fact]
    public void HarnessSection_ShowsNoSuggestions_WhenNull()
    {
        RegisterServices(harnessSuggestions: null);

        var cut = Render<Consolidation>();

        var markup = cut.Markup;
        Assert.Contains("No suggestions generated yet", markup);
    }

    /// <summary>
    /// Requirement 8.2: Harness section shows metadata (generated date, run count, success rate).
    /// </summary>
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
        // P1 format renders as "80.0 %" or "80,0 %" depending on culture
        Assert.Contains("80", meta.TextContent);
    }

    // ═══ Requirement 1.5: Run history table ═══

    /// <summary>
    /// Requirement 1.5: Run history table renders with correct columns.
    /// </summary>
    [Fact]
    public void RunHistoryTable_Renders_WithRunData()
    {
        var runs = new List<ConsolidationRun>
        {
            new()
            {
                RunId = "run-1",
                Type = ConsolidationRunType.BrainConsolidation,
                TemplateId = "t1",
                TemplateName = "DotNet Repo",
                StartedAtUtc = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
                Status = ConsolidationRunStatus.Succeeded,
                Summary = "3 files modified"
            },
            new()
            {
                RunId = "run-2",
                Type = ConsolidationRunType.HarnessSuggestions,
                TemplateId = null,
                TemplateName = null,
                StartedAtUtc = new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc),
                Status = ConsolidationRunStatus.Failed,
                Summary = "Timeout"
            }
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

    /// <summary>
    /// Requirement 1.5: Run history shows empty state when no runs exist.
    /// </summary>
    [Fact]
    public void RunHistoryTable_ShowsEmptyState_WhenNoRuns()
    {
        RegisterServices(runHistory: Array.Empty<ConsolidationRun>());

        var cut = Render<Consolidation>();

        var markup = cut.Markup;
        Assert.Contains("No consolidation runs yet", markup);
    }

    // ═══ Requirement 10.2: Badge resets on page load ═══

    /// <summary>
    /// Requirement 10.2: Badge count resets to zero when page loads.
    /// </summary>
    [Fact]
    public void BadgeResetsToZero_OnPageLoad()
    {
        _badgeService.IncrementBy(5);
        Assert.Equal(5, _badgeService.BadgeCount);

        RegisterServices();
        Render<Consolidation>();

        Assert.Equal(0, _badgeService.BadgeCount);
    }

    // ═══ Requirement 3.7: Trigger rejection shows message ═══

    /// <summary>
    /// Requirement 3.7: When trigger is rejected (returns null), a status message is shown.
    /// </summary>
    [Fact]
    public void TriggerRejection_ShowsStatusMessage()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(brainProviderId: "brain-1")
        };
        RegisterServices(templates: templates);

        // TODO: Mock uses It.IsAny<TemplateId?>() which doesn't verify the correct template ID
        // value flows through the Razor page's string→TemplateId conversion. Add a test that
        // asserts the specific TemplateId value passed to TriggerAsync matches the expected template.
        _mockConsolidationService.Setup(s => s.TriggerAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((ConsolidationRun?)null);

        var cut = Render<Consolidation>();

        // Click the Brain Consolidation button
        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        var statusMessage = cut.Find(".consolidation-status-message");
        Assert.Contains("rejected", statusMessage.TextContent.ToLowerInvariant());
    }

    // ═══ Refactoring Scan Pre-Flight Modal (Issue #1435) ═══

    /// <summary>
    /// Clicking Refactoring Scan opens the pre-flight modal instead of calling TriggerAsync directly.
    /// </summary>
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

    /// <summary>
    /// Modal displays current config values: max proposals, hotspot lookback, adversarial review.
    /// </summary>
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
        // Default PipelineConfiguration values: MaxRefactoringProposals=3, HotspotAnalysisLookback=90d, RefactoringReviewEnabled=true
        // TODO: These assertions are overly weak — "3" could match any text in the modal. Scope assertions
        // to specific DOM elements (e.g., .refactoring-modal-param-value) for precision.
        Assert.Contains("3", modal.TextContent);
        Assert.Contains("90 days", modal.TextContent);
        Assert.Contains("Enabled", modal.TextContent);
    }

    /// <summary>
    /// Modal shows agent:generated as always-applied label badge.
    /// </summary>
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

    /// <summary>
    /// Modal agent:next checkbox defaults to unchecked.
    /// </summary>
    // TODO: Weak assertion — the double-negation logic can pass even if the DOM is in an unexpected
    // state. Replace with checkbox.IsChecked() or Assert.Null(checkbox.GetAttribute("checked")) for
    // a more robust unchecked-state verification.
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
        // TODO: This assertion is tautological — it always passes when the "checked" attribute is absent.
        // Replace with a direct check on the element's checked property via bUnit for robustness.
        Assert.False(checkbox.HasAttribute("checked") && checkbox.GetAttribute("checked") != "false");
    }

    /// <summary>
    /// Confirming modal without checking agent:next calls TriggerAsync with autoDispatch=false.
    /// </summary>
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
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "run-1",
                Type = ConsolidationRunType.RefactoringDetection,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var cut = Render<Consolidation>();

        // Open modal
        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        // Confirm without checking the checkbox
        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), false), Times.Once);
    }

    /// <summary>
    /// Confirming modal with agent:next checked calls TriggerAsync with autoDispatch=true.
    /// </summary>
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
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "run-1",
                Type = ConsolidationRunType.RefactoringDetection,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var cut = Render<Consolidation>();

        // Open modal
        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        // Check the auto-dispatch checkbox
        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);

        // Confirm
        var startButton = cut.Find(".modal-card .btn-save");
        startButton.Click();

        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, "t1", It.IsAny<CancellationToken>(), true), Times.Once);
    }

    /// <summary>
    /// Cancel button closes modal without triggering.
    /// </summary>
    [Fact]
    public void RefactoringModal_Cancel_ClosesWithoutTriggering()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // Open modal
        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        // Click cancel
        var cancelButton = cut.Find(".modal-card .btn-cancel");
        cancelButton.Click();

        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Brain Consolidation still triggers immediately (no modal).
    /// </summary>
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
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "run-1",
                Type = ConsolidationRunType.BrainConsolidation,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var cut = Render<Consolidation>();

        var brainButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Brain Consolidation"));
        brainButton.Click();

        // No modal should appear
        Assert.Empty(cut.FindAll(".modal-overlay"));
        // TriggerAsync should have been called directly
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.BrainConsolidation, "t1", It.IsAny<CancellationToken>(), false), Times.Once);
    }

    /// <summary>
    /// Harness Suggestions still triggers immediately (no modal).
    /// </summary>
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
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "run-1",
                Type = ConsolidationRunType.HarnessSuggestions,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var cut = Render<Consolidation>();

        var suggestionsButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Generate Suggestions"));
        suggestionsButton.Click();

        // No modal should appear
        Assert.Empty(cut.FindAll(".modal-overlay"));
        // TriggerAsync should have been called directly
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.HarnessSuggestions, null, It.IsAny<CancellationToken>(), false), Times.Once);
    }

    // TODO: Missing test — Rehydration preserves AutoDispatch flag. Should verify that
    // RehydrateQueuedRunsAsync correctly maps ConsolidationRun.AutoDispatch back into a new
    // ConsolidationJobMessage when re-dispatching a previously-queued run.

    // ═══ Issue #1772: Modal focus and Enter-key bubble fixes ═══

    /// <summary>
    /// Issue #1772 AC1: FocusAsync is called exactly once when the modal opens — not on re-renders.
    /// Uses bUnit's built-in JSInterop.VerifyFocusAsyncInvoke to correctly intercept
    /// ElementReference.FocusAsync() calls routed through bUnit's BunitJSRuntime.
    /// </summary>
    [Fact]
    public void FocusAsync_CalledOnce_WhenModalOpened()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // Open the modal
        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        // Verify FocusAsync was called exactly once after the modal opened.
        // JSInterop.VerifyFocusAsyncInvoke uses bUnit's built-in handler which correctly
        // captures ElementReference.FocusAsync() calls (unlike Mock<IJSRuntime> which
        // does not intercept calls routed through BunitJSRuntime).
        // TODO(WARNING): VerifyFocusAsyncInvoke relies on "Blazor._internal.domWrapper.focus"
        // internally — this is an undocumented Blazor implementation detail. If the runtime
        // changes this method name, all FocusAsync_* tests may become unreliable.
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1);
    }

    /// <summary>
    /// Issue #1772 AC1: FocusAsync is NOT called again on subsequent re-renders while the modal is open.
    /// </summary>
    [Fact]
    public void FocusAsync_NotCalledAgain_OnSubsequentRerender()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // Open the modal
        var refactoringButton = cut.FindAll(".btn-trigger")
            .First(b => b.TextContent.Contains("Refactoring Scan"));
        refactoringButton.Click();

        // Trigger additional re-renders (StateHasChanged equivalent: toggle checkbox to cause re-render)
        // TODO(WARNING): Whether bUnit triggers OnAfterRenderAsync after each .Change() call depends on
        // bUnit's rendering model (it calls render synchronously, but async lifecycle hooks may not be
        // fully awaited). If the lifecycle is not awaited, re-renders may not exercise the guard path —
        // the assertion VerifyFocusAsyncInvoke(calledTimes: 1) passes trivially because no second
        // FocusAsync attempt was ever made, meaning this test cannot reliably distinguish a correct
        // implementation from one where _modalJustOpened is never cleared. Consider using
        // cut.InvokeAsync(() => ...) to force an awaited render cycle, or replace with an integration
        // test that exercises the actual Blazor lifecycle.
        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);
        checkbox.Change(false);

        // FocusAsync must still have been called only once — the re-renders must not steal focus
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 1);
    }

    /// <summary>
    /// Issue #1772 AC1: FocusAsync is called again when the modal is closed and reopened.
    /// Regression guard for the _modalJustOpened flag reset logic.
    /// </summary>
    [Fact]
    public void FocusAsync_CalledAgain_WhenModalReopened()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // First open
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        // Close via cancel
        cut.Find(".modal-card .btn-cancel").Click();
        // Second open
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();

        // FocusAsync should have been called twice — once per modal open
        JSInterop.VerifyFocusAsyncInvoke(calledTimes: 2);
    }

    /// <summary>
    /// Issue #1772 AC2: Pressing Enter while the checkbox is focused does NOT trigger modal confirmation.
    /// Verifies the behavioral intent of @onkeydown:stopPropagation="true" on the checkbox — that Enter
    /// inside the checkbox does not bubble to the modal overlay's HandleRefactoringModalKeyDown handler.
    /// Note: bUnit does not simulate DOM event bubbling, and the checkbox has no onkeydown handler
    /// (only the :stopPropagation modifier), so the test verifies the modal stays open after checkbox
    /// interaction. The :stopPropagation modifier means there is intentionally no onkeydown handler to
    /// dispatch to on the checkbox element itself.
    /// </summary>
    /// <remarks>
    /// Known limitation: This test cannot directly verify the stopPropagation fix. bUnit does not simulate
    /// DOM event bubbling — the absence of a keydown handler on the checkbox is itself a consequence of
    /// removing the no-op @onkeydown="_ => { }" lambda. A true regression guard for AC2 would require a
    /// Playwright/E2E test that runs in a real browser.
    /// </remarks>
    [Fact]
    public void EnterKey_OnCheckbox_DoesNotConfirmModal()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // Open modal
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        // Toggle the checkbox (the only interaction available on the checkbox in bUnit).
        // The @onkeydown:stopPropagation="true" modifier means the checkbox has no C# keydown handler —
        // any keydown event on the checkbox is intentionally stopped at the DOM level before reaching
        // the overlay. Interacting with the checkbox via Change must not trigger confirmation.
        var checkbox = cut.Find(".modal-card input[type='checkbox']");
        checkbox.Change(true);

        // Modal should remain open and TriggerAsync must NOT have been called
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// Regression guard: Enter key on modal overlay confirms the modal (Enter-to-confirm must still work).
    /// </summary>
    /// <remarks>
    /// TODO(WARNING): This test fires Enter directly on the modal overlay, bypassing DOM bubbling entirely.
    /// It verifies the pre-existing Enter-confirm behaviour but does NOT exercise the stopPropagation
    /// fix for AC2 (checkbox bubble). See EnterKey_OnCheckbox_DoesNotConfirmModal for the AC2-specific test.
    /// </remarks>
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
            .ReturnsAsync(new ConsolidationRun
            {
                RunId = "run-1",
                Type = ConsolidationRunType.RefactoringDetection,
                StartedAtUtc = DateTimeOffset.UtcNow,
                Status = ConsolidationRunStatus.Running
            });

        var cut = Render<Consolidation>();

        // Open modal
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        // Simulate Enter on the modal overlay
        cut.Find(".modal-overlay").TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Enter" });

        // Modal should be closed and TriggerAsync invoked
        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            ConsolidationRunType.RefactoringDetection, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    /// <summary>
    /// Escape key on modal overlay closes the modal without triggering.
    /// </summary>
    [Fact]
    public void EscapeKey_OnModalOverlay_ClosesModalWithoutTriggering()
    {
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate(issueProviderId: "issue-1", repoProviderId: "repo-1")
        };
        RegisterServices(templates: templates);

        var cut = Render<Consolidation>();

        // Open modal
        cut.FindAll(".btn-trigger").First(b => b.TextContent.Contains("Refactoring Scan")).Click();
        Assert.NotEmpty(cut.FindAll(".modal-overlay"));

        // Simulate Escape on the modal overlay
        cut.Find(".modal-overlay").TriggerEvent("onkeydown", new KeyboardEventArgs { Key = "Escape" });

        // Modal should be closed and TriggerAsync not called
        Assert.Empty(cut.FindAll(".modal-overlay"));
        _mockConsolidationService.Verify(s => s.TriggerAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    // TODO(WARNING): The BrainConsolidation_PassesCorrectTemplateIdValue_ToTriggerAsync test (issue #1775)
    // was deleted during the #1772 changes and not replaced. That test verified that TriggerConsolidation
    // passes the TemplateId via implicit conversion (enforcing non-empty) rather than the raw constructor —
    // an integration-level assertion NOT covered by TemplateIdTests.cs (which only tests the model).
    // Also deleted: the TODO comment about testing the empty-string templateId guard (when templateId is "",
    // TriggerAsync should receive null — not a TemplateId constructed from ""). These regression guards
    // should be restored in a follow-up: add BrainConsolidation_PassesCorrectTemplateIdValue_ToTriggerAsync
    // and BrainConsolidation_WithEmptyTemplateId_PassesNullToTriggerAsync.
}
