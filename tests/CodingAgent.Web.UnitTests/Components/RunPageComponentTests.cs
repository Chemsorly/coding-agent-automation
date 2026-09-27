using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for RunPage verifying that BuildRunModelFromSummary correctly propagates
/// RunType from the summary to the PipelineRun view model, so the PipelineSidebar renders
/// the correct phase groups for decomposition runs. See issue #2600.
/// Also covers issue #2948: output tail rendering and re-dispatch button flow.
/// </summary>
public class RunPageComponentTests : BunitContext
{
    // ── Shared scaffolding ────────────────────────────────────────────────

    /// <summary>
    /// Registers all services required by RunPage into bUnit's service collection,
    /// configured so that GetRunAsync returns the provided summary and the page renders
    /// in a completed (non-live) state. Uses a default (no-setup) WorkItems mock.
    /// </summary>
    private void RegisterServices(PipelineRunSummary summary)
        => RegisterServices(summary, new Mock<IPipelineApiWorkItemClient>());

    /// <summary>
    /// Overload accepting a pre-configured IPipelineApiWorkItemClient mock, used by
    /// re-dispatch tests that need to assert on DispatchAsync calls.
    /// </summary>
    private void RegisterServices(PipelineRunSummary summary, Mock<IPipelineApiWorkItemClient> mockWorkItems)
    {
        var mockHub = new Mock<IAgentHubConnection>();
        mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory
            .Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        Services.AddSingleton(mockHub.Object);
        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(Mock.Of<IConfigurationStore>());
        Services.AddSingleton(new CockpitState());
    }

    /// <summary>
    /// Builds a minimal completed PipelineRunSummary with the specified RunType and FinalStep.
    /// </summary>
    private static PipelineRunSummary MakeSummary(PipelineRunType runType, PipelineStep finalStep)
    {
        var runId = Guid.NewGuid().ToString();
        // TODO [WARNING]: BrainRepoUsed is left false (default), so BrainProviderConfigId will be null on the
        // resulting model, causing SyncingBrainRepoPreRun and SyncingBrainRepoPostRun steps to be hidden by
        // the brain-provider guard. This differs silently from a typical real decomp run. Consider adding a
        // MakeSummary overload or a separate test that exercises BrainRepoUsed = true.
        return new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "2600",
            IssueTitle = "Decomp test issue",
            FinalStep = finalStep,
            RunType = runType,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            // TODO [WARNING]: DecompositionSubIssuesAttempted is left at 0. The acceptance criterion
            // "The sub-issues stat row renders when DecompositionSubIssuesAttempted > 0 on a decomp run"
            // is not covered. Add a test that sets DecompositionSubIssuesAttempted > 0 and asserts the
            // stat row is visible, and another that confirms it is absent when the value is 0.
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When GetRunAsync returns a summary with RunType = DecompositionAnalysis,
    /// BuildRunModelFromSummary must propagate that RunType to the PipelineRun view model
    /// so PipelineSidebar renders the "Decomposition Analysis" phase group and hides the
    /// implementation-only phase groups.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithDecompositionAnalysisSummary_SetsRunTypeOnModel()
    {
        var summary = MakeSummary(PipelineRunType.DecompositionAnalysis, PipelineStep.PostingPlan);
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Positive: decomposition-analysis phase must be visible
        // TODO [WARNING]: The positive assertion verifies sidebar rendering but does not directly assert
        // model.RunType == PipelineRunType.DecompositionAnalysis. If IsHiddenForDecompositionRun is later
        // changed, tests could continue to pass even if RunType propagation were broken. Consider also
        // asserting on the run-type badge element (AC-3: "Decomp" badge) for stronger coverage of the
        // stated acceptance criterion. The Preparation phase (Created, CloningRepository, etc.) is not
        // checked for absence — it correctly remains visible for decomp runs, but is not explicitly
        // asserted here.
        Assert.NotNull(cut.Find("[data-testid='phase-decomposition-analysis']"));

        // Negative: implementation-only phases must be hidden by IsHiddenForDecompositionRun
        Assert.Empty(cut.FindAll("[data-testid='phase-analysis']"));
        Assert.Empty(cut.FindAll("[data-testid='phase-code-generation']"));
    }

    /// <summary>
    /// When GetRunAsync returns a summary with RunType = Decomposition,
    /// BuildRunModelFromSummary must propagate that RunType to the PipelineRun view model
    /// so PipelineSidebar renders the "Decomposition" phase group and hides the
    /// implementation-only phase groups.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithDecompositionSummary_SetsRunTypeOnModel()
    {
        var summary = MakeSummary(PipelineRunType.Decomposition, PipelineStep.PostingSummary);
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Positive: decomposition phase must be visible
        // TODO [WARNING]: Same as above — consider also asserting on the run-type badge (AC-3) and the
        // broader phase visibility (e.g., Preparation phase remains visible for decomp runs, which is
        // correct, but is not explicitly guarded here).
        Assert.NotNull(cut.Find("[data-testid='phase-decomposition']"));

        // Negative: implementation-only phases must be hidden by IsHiddenForDecompositionRun
        Assert.Empty(cut.FindAll("[data-testid='phase-analysis']"));
        Assert.Empty(cut.FindAll("[data-testid='phase-code-generation']"));
    }

    // ── BuildRunModelFromSummary seeding tests (issue #2936) ──────────────

    /// <summary>
    /// BuildRunModelFromSummary must seed CompletedAtOffset so the Duration detail item shows a
    /// formatted duration (e.g. "4h 32m") instead of "running" for a completed run.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithCompletedSummary_DurationIsNotRunning()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2936",
            IssueTitle = "Duration test",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddHours(-4).AddMinutes(-32),
            CompletedAtOffset = now,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        var durationText = cut.Find(".cockpit-detail-item .v").TextContent;
        Assert.NotEqual("running", durationText);
        // Should render in the "Xh Ym" format for a ~4h32m run.
        // TODO: [WARNING] .Find(".cockpit-detail-item .v") latches onto the *first* matching element.
        // If another detail item is inserted before Duration this silently targets the wrong element.
        // Add a data-testid to the Duration item and use FindByTestId() for an unambiguous selector.
        Assert.Matches(@"\d+h \d+m", durationText);
    }

    /// <summary>
    /// BuildRunModelFromSummary must seed BrainContextLoaded and BrainKnowledgeFileCount so the
    /// SyncingBrainRepoPreRun step shows "N knowledge files loaded" instead of "Brain context unavailable".
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithBrainContextLoaded_ShowsKnowledgeFileCount()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2936",
            IssueTitle = "Brain test",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-5),
            CompletedAtOffset = now,
            BrainRepoUsed = true,
            BrainContextLoaded = true,
            BrainKnowledgeFileCount = 7,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        var brainStepText = cut.Find("#step-SyncingBrainRepoPreRun").TextContent;
        // TODO: [WARNING] Confirm BuildRunModelFromSummary seeds BrainRepoUsed onto the model;
        // if it does not, PipelineSidebar won't render #step-SyncingBrainRepoPreRun and Find()
        // will throw — failing for the wrong reason (missing element) rather than the assertion.
        Assert.Contains("7 knowledge files loaded", brainStepText);
        Assert.DoesNotContain("Brain context unavailable", brainStepText);
    }

    /// <summary>
    /// For a failed run with LastActiveStep set, BuildRunModelFromSummary must use that step as
    /// HighWaterMark so the sidebar marks the correct step as failed (not a fabricated one).
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithFailedRunAndLastActiveStep_ShowsCorrectFailureStep()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2936",
            IssueTitle = "Failure step test",
            FinalStep = PipelineStep.Failed,
            LastActiveStep = PipelineStep.GeneratingCode,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-10),
            CompletedAtOffset = now,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // GeneratingCode must be marked as failed (it is the last persisted step).
        Assert.Contains("step-card-failed", cut.Find("#step-GeneratingCode").GetAttribute("class"));
        // ReviewingAnalysis must NOT be marked as failed — the pre-fix fabrication bug.
        // TODO: [WARNING] This negative assertion only guards ReviewingAnalysis. The pre-fix heuristic
        // could also fabricate AnalyzingCode or SyncingBrainRepoPreRun. Add negative assertions for
        // those steps, or assert that exactly one step (GeneratingCode) carries step-card-failed.
        Assert.DoesNotContain("step-card-failed", cut.Find("#step-ReviewingAnalysis").GetAttribute("class") ?? "");
    }

    /// <summary>
    /// For a failed run with no LastActiveStep (old row), BuildRunModelFromSummary must not
    /// fabricate a failure at ReviewingAnalysis or other mid-pipeline steps.
    /// HighWaterMark is set to Created (ordinal 0) so GetLastReachedStep returns Created —
    /// the sidebar marks Created (the start) as failed, not an invented downstream step.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithFailedRunAndNoLastActiveStep_ShowsNoFabricatedFailureStep()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2936",
            IssueTitle = "No fabricated step test",
            FinalStep = PipelineStep.Failed,
            LastActiveStep = null,  // old row: no persisted last step
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-10),
            CompletedAtOffset = now,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // With no LastActiveStep, HighWaterMark = Created, so no mid-pipeline step should be fabricated.
        // ReviewingAnalysis was the pre-fix fabrication — it must NOT be marked as failed.
        // TODO: [WARNING] These negative assertions don't confirm what IS marked. If HighWaterMark
        // falls back to Created, only the Created step should be failed. Add a positive assertion
        // that #step-Created carries step-card-failed to close this gap and prevent silent regressions.
        Assert.DoesNotContain("step-card-failed", cut.Find("#step-ReviewingAnalysis").GetAttribute("class") ?? "");
        Assert.DoesNotContain("step-card-failed", cut.Find("#step-GeneratingCode").GetAttribute("class") ?? "");
        Assert.DoesNotContain("step-card-failed", cut.Find("#step-RunningQualityGates").GetAttribute("class") ?? "");
    }

    /// <summary>
    /// BuildRunModelFromSummary must seed BranchName from the summary so that the PipelineSidebar
    /// renders the feature branch name in the CreatingBranch step.
    /// Issue #2947 regression: BranchName was not being seeded, leaving it null even when present.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithBranchName_SeedsBranchNameOnModel()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2947",
            IssueTitle = "Branch name seeding test",
            FinalStep = PipelineStep.Completed,
            LastActiveStep = PipelineStep.CreatingBranch,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-5),
            CompletedAtOffset = now,
            BranchName = "feature/issue-2947-branch-test",
            PullRequestUrl = "https://github.com/owner/repo/pull/123",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // The links rail must contain the PR link (PullRequestUrl is present).
        var markup = cut.Markup;
        Assert.Contains("Pull request", markup);
        // TODO: [WARNING] PullRequestUrl assertion belongs in BuildRunModelFromSummary_WithPullRequestUrlOnFailedRun_ShowsPrLinkInRail,
        // not here. This test should only verify BranchName seeding; conflating both properties
        // means a PullRequestUrl rendering regression fails with a misleading BranchName message.
        // Split into isolated tests so each failure is unambiguous. (TestQualityReviewer, issue #2947)
        Assert.Contains("https://github.com/owner/repo/pull/123", markup);

        // TODO: [WARNING] Assert.Contains on raw markup is fragile — the branch name could appear
        // anywhere (aria label, data attribute, debug dump) and still pass even if the intended
        // UI path (PipelineSidebar CreatingBranch step detail) is broken. Replace with a narrower
        // selector such as cut.Find("[data-testid='branch-name']").TextContent to pin the rendering
        // location. (TestQualityReviewer, issue #2947)
        // The branch name must be visible in the page markup (rendered by PipelineSidebar
        // inside the CreatingBranch step detail when BranchName is set on the model).
        Assert.Contains("feature/issue-2947-branch-test", markup);
    }

    /// <summary>
    /// When a run summary has PullRequestUrl set (e.g. a run that failed after PR creation),
    /// the links rail must show the PR link so operators can navigate to it.
    /// Issue #2947 regression: PullRequestUrl was already seeded but BranchName was not.
    /// This test locks in the PullRequestUrl seeding behavior.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithPullRequestUrlOnFailedRun_ShowsPrLinkInRail()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2947",
            IssueTitle = "PR link after failure test",
            FinalStep = PipelineStep.ConflictRestart,
            LastActiveStep = PipelineStep.RunningQualityGates,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-10),
            CompletedAtOffset = now,
            PullRequestUrl = "https://github.com/owner/repo/pull/99",
            FailureReason = "PR conflicted with main — restarting pipeline",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // The links rail must contain the PR link even for terminal non-Completed states.
        // TODO: [WARNING] Only PipelineStep.ConflictRestart is tested here. The null-preservation
        // fix in JobCompletionMapper.Apply is intended to protect all terminal paths that send
        // PullRequestUrl = null (e.g. Failed, Exhausted). A parameterised test over several
        // terminal PipelineStep values would provide broader coverage and prevent an accidentally
        // too-narrow fix (e.g. guard only on ConflictRestart) from going undetected.
        // (TestQualityReviewer, issue #2947)
        Assert.Contains("Pull request", cut.Markup);
        Assert.Contains("https://github.com/owner/repo/pull/99", cut.Markup);
    }

    /// <summary>
    /// For a completed run, both the Duration detail item and the elapsed display (via Dur helper)
    /// must reflect the real StartedAt→CompletedAt span, not a live DateTimeOffset.UtcNow-based value.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithCompletedRun_ElapsedMatchesDuration()
    {
        // Use a fixed window so the assertion is stable regardless of test execution time.
        var start = DateTimeOffset.UtcNow.AddHours(-4).AddMinutes(-32);
        var end = start.AddHours(4).AddMinutes(32);
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2936",
            IssueTitle = "Elapsed test",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = start,
            CompletedAtOffset = end,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Duration detail item must show "4h 32m" (not "running").
        // TODO: [WARNING] .Find(".cockpit-detail-item .v") latches onto the *first* matching element —
        // fragile selector, same issue as BuildRunModelFromSummary_WithCompletedSummary_DurationIsNotRunning.
        // Add a data-testid to the Duration item and use FindByTestId() for an unambiguous selector.
        // TODO: [WARNING] No test covers the live-timer path (PeriodicTimer / StateHasChanged advancing
        // Elapsed on active runs). The acceptance criterion "Elapsed advances every second" has zero
        // automated coverage. Consider adding an abstraction over PeriodicTimer so bUnit tests can fake
        // tick progression and assert that the elapsed value changes.
        var durationText = cut.Find(".cockpit-detail-item .v").TextContent;
        Assert.Equal("4h 32m", durationText);
    }

    // ── Issue #2948: Output tail card ─────────────────────────────────────

    /// <summary>
    /// When a terminal run has an OutputTail in the summary, the page must render a
    /// "Agent output" card containing a pre.run-live-log element with the tail lines.
    /// </summary>
    [Fact]
    public void OutputTail_WithTerminalRunHavingOutputLines_RendersOutputCard()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Output tail test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            OutputTail = ["first line", "second line", "third line"]
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        var outputCard = cut.Find("[data-testid='output-tail-card']");
        Assert.NotNull(outputCard);
        var pre = cut.Find("pre.run-live-log");
        Assert.NotNull(pre);
        Assert.Contains("first line", pre.TextContent);
        Assert.Contains("third line", pre.TextContent);
    }

    /// <summary>
    /// When a terminal run has no OutputTail (null), the output card must not render.
    /// </summary>
    [Fact]
    public void OutputTail_WithTerminalRunHavingNoOutputTail_DoesNotRenderOutputCard()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "No output tail test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            OutputTail = null
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.Empty(cut.FindAll("[data-testid='output-tail-card']"));
        Assert.Empty(cut.FindAll("pre.run-live-log"));
    }

    // ── Issue #2948: Re-dispatch button ──────────────────────────────────
    // TODO: [WARNING] Missing negative test: a Review or Decomposition run type with FinalStep=Failed and
    // provider IDs should NOT show the re-dispatch button (CanRedispatch gates on RunType==Implementation).
    // Without this test, broadening the run-type check would go undetected.
    // TODO: [WARNING] Missing positive test: a ConflictRestart run should show the re-dispatch button.
    // CanRedispatch includes PipelineStep.ConflictRestart but no bUnit test covers that branch.

    /// <summary>
    /// A failed Implementation run with provider IDs must show the re-dispatch button.
    /// </summary>
    [Fact]
    public void ReDispatch_FailedRun_ShowsReDispatchButton()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Re-dispatch test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.NotEmpty(cut.FindAll("[data-testid='redispatch-btn']"));
    }

    /// <summary>
    /// A cancelled Implementation run with provider IDs must also show the re-dispatch button.
    /// </summary>
    [Fact]
    public void ReDispatch_CancelledRun_ShowsReDispatchButton()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Cancelled re-dispatch test",
            FinalStep = PipelineStep.Cancelled,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.NotEmpty(cut.FindAll("[data-testid='redispatch-btn']"));
    }

    /// <summary>
    /// A completed run must NOT show the re-dispatch button.
    /// </summary>
    [Fact]
    public void ReDispatch_CompletedRun_HidesReDispatchButton()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Completed run test",
            FinalStep = PipelineStep.Completed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.Empty(cut.FindAll("[data-testid='redispatch-btn']"));
        Assert.Empty(cut.FindAll("[data-testid='redispatch-card']"));
    }

    /// <summary>
    /// A failed run with missing IssueProviderConfigId (old run, no provider IDs) must NOT
    /// show the re-dispatch button — cannot construct a valid dispatch request without them.
    /// </summary>
    [Fact]
    public void ReDispatch_FailedRunMissingProviderIds_NoReDispatchButton()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Missing provider IDs test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = null,   // old run: no provider IDs
            RepoProviderConfigId = null,
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.Empty(cut.FindAll("[data-testid='redispatch-btn']"));
        Assert.Empty(cut.FindAll("[data-testid='redispatch-card']"));
    }

    /// <summary>
    /// Clicking Re-dispatch then Confirm must call WorkItems.DispatchAsync once with the
    /// correct IssueIdentifier from the summary.
    /// </summary>
    [Fact]
    public async Task ReDispatch_ClickConfirm_CallsDispatchAsync()
    {
        var dispatchedRequest = (JobDistributionRequest?)null;
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems
            .Setup(w => w.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => dispatchedRequest = req)
            .ReturnsAsync(Guid.NewGuid());

        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "org/repo#2948",
            IssueTitle = "Re-dispatch confirm test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary, mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Click the "Re-dispatch" button to show the confirm section
        await cut.InvokeAsync(() => cut.Find("[data-testid='redispatch-btn']").Click());

        // Click the "Confirm re-dispatch" button
        await cut.InvokeAsync(() => cut.Find("[data-testid='redispatch-confirm-btn']").Click());

        // Verify DispatchAsync was called once with the correct IssueIdentifier
        mockWorkItems.Verify(w => w.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(dispatchedRequest);
        Assert.Equal("org/repo#2948", (string)dispatchedRequest!.IssueIdentifier);
        Assert.Equal("ip-1", dispatchedRequest.IssueProviderConfigId);
        Assert.Equal("rp-1", dispatchedRequest.RepoProviderConfigId);
        Assert.Equal(WorkItemTaskType.Implementation, dispatchedRequest.TaskType);
        Assert.Equal(PipelineRunType.Implementation, dispatchedRequest.RunType);
    }

    /// <summary>
    /// When DispatchAsync throws, the page must render an error message and not crash.
    /// </summary>
    [Fact]
    public async Task ReDispatch_DispatchFails_ShowsError()
    {
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems
            .Setup(w => w.DispatchAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("503 Service Unavailable"));

        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2948",
            IssueTitle = "Dispatch failure test",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary, mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Click Re-dispatch → Confirm
        await cut.InvokeAsync(() => cut.Find("[data-testid='redispatch-btn']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='redispatch-confirm-btn']").Click());

        // An error message must be rendered; the re-dispatch button must still be available
        // TODO: [WARNING] The claim "re-dispatch button must still be available" is not asserted. After a
        // failure _showRedispatchConfirm is still true so the confirm dialog (redispatch-confirm-btn) is
        // visible, not the initial redispatch-btn. Assert that redispatch-confirm-btn is present so a
        // regression that hides all re-dispatch UI on error would be caught.
        // TODO: [WARNING] No assertion that the success banner is absent. An implementation that sets
        // _redispatchSuccess=true on failure would show both callouts and still pass this test. Add
        // Assert.Empty(cut.FindAll(".agent-detail-confirm")) to guard against that.
        var errorCallout = cut.Find(".summary-failure-callout");
        Assert.NotNull(errorCallout);
        Assert.Contains("Re-dispatch failed", errorCallout.TextContent);
    }

    // ── Issue #2937: RunPage cancel confirmation ──────────────────────────────

    /// <summary>
    /// Helper to build a minimal active (in-flight) PipelineRunSummary for cancel tests.
    /// FinalStep = AnalyzingCode (not a terminal step) so _isLive becomes true and the sidebar's
    /// cancel button is rendered.
    /// </summary>
    private static PipelineRunSummary MakeActiveSummary(string runId)
        => new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "2937",
            IssueTitle = "Active run cancel test",
            FinalStep = PipelineStep.AnalyzingCode,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = null,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };

    /// <summary>
    /// On an active run, clicking "Cancel Pipeline" in the sidebar must show the confirmation
    /// prompt; PostStatusAsync must NOT be called until "Yes, cancel" is clicked.
    /// </summary>
    // TODO: [WARNING] This test does not assert that the original cancel-pipeline-btn is hidden
    // once the confirmation section is shown. A regression rendering both simultaneously would
    // pass this test. Consider adding: Assert.Empty(cut.FindAll("[data-testid='cancel-pipeline-btn']"))
    // after the confirmation section appears.
    [Fact]
    public void CancelRun_ClickCancelPipeline_ShowsConfirmation_NoCancelYet()
    {
        var runId = Guid.NewGuid().ToString();
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        RegisterServices(MakeActiveSummary(runId), mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, runId));

        // The "Cancel Pipeline" button is rendered by PipelineSidebar when IsRunning=true
        cut.Find("[data-testid='cancel-pipeline-btn']").Click();

        // Confirmation section must appear; Yes/No buttons visible
        Assert.NotEmpty(cut.FindAll("[data-testid='cancel-pipeline-confirm-section']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='confirm-cancel-pipeline-btn']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='dismiss-cancel-pipeline-btn']"));

        // PostStatusAsync must NOT have been called yet
        mockWorkItems.Verify(
            w => w.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Clicking "Yes, cancel" after the confirmation must call PostStatusAsync once with Cancelled.
    /// </summary>
    [Fact]
    public async Task CancelRun_ClickConfirm_CallsPostStatusOnce()
    {
        var runId = Guid.NewGuid().ToString();
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems
            .Setup(w => w.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        RegisterServices(MakeActiveSummary(runId), mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, runId));

        // Show the confirmation
        await cut.InvokeAsync(() => cut.Find("[data-testid='cancel-pipeline-btn']").Click());

        // Confirm
        await cut.InvokeAsync(() => cut.Find("[data-testid='confirm-cancel-pipeline-btn']").Click());

        mockWorkItems.Verify(
            w => w.PostStatusAsync(
                It.Is<Guid>(g => g.ToString() == runId),
                It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled"),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "PostStatusAsync must be called exactly once with Cancelled after confirming");
    }

    /// <summary>
    /// Clicking "No" (dismiss) must hide the confirmation and NOT call PostStatusAsync.
    /// </summary>
    [Fact]
    public void CancelRun_ClickDismiss_HidesConfirmation_NoCancelCalled()
    {
        var runId = Guid.NewGuid().ToString();
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        RegisterServices(MakeActiveSummary(runId), mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, runId));

        cut.Find("[data-testid='cancel-pipeline-btn']").Click();

        // Dismiss
        cut.Find("[data-testid='dismiss-cancel-pipeline-btn']").Click();

        // Confirmation section must be gone; Cancel Pipeline button back
        Assert.Empty(cut.FindAll("[data-testid='cancel-pipeline-confirm-section']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='cancel-pipeline-btn']"));

        // PostStatusAsync must not have been called
        mockWorkItems.Verify(
            w => w.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// When PostStatusAsync throws, an error callout must be rendered on the RunPage.
    /// </summary>
    [Fact]
    public async Task CancelRun_PostStatusFails_ShowsErrorCallout()
    {
        var runId = Guid.NewGuid().ToString();
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems
            .Setup(w => w.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("503 Service Unavailable"));
        RegisterServices(MakeActiveSummary(runId), mockWorkItems);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, runId));

        await cut.InvokeAsync(() => cut.Find("[data-testid='cancel-pipeline-btn']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='confirm-cancel-pipeline-btn']").Click());

        // Error callout must be visible
        var callout = cut.Find("[data-testid='cancel-error-callout']");
        Assert.NotNull(callout);
        Assert.Contains("Cancel failed", callout.TextContent);
        Assert.Contains("503 Service Unavailable", callout.TextContent);
    }

    // ── Terminal-like final steps ─────────────────────────────────────────

    /// <summary>
    /// Restarted, merged and closed runs are finished: the page must show their outcome badge and must
    /// not offer "Cancel Pipeline" or a live-output panel that waits forever.
    /// </summary>
    [Theory]
    [InlineData(PipelineStep.ConflictRestart, "Restarted")]
    [InlineData(PipelineStep.PrMerged, "Merged")]
    [InlineData(PipelineStep.PrClosed, "Closed")]
    public void TerminalLikeRun_ShowsOutcome_WithoutCancelOrLiveOutput(PipelineStep finalStep, string expectedBadge)
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "3019",
            IssueTitle = "Terminal-like run",
            FinalStep = finalStep,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-2),
            CompletedAtOffset = DateTimeOffset.UtcNow.AddHours(-1),
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        Assert.Equal(expectedBadge, cut.Find(".cockpit-page-header .step-badge").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='cancel-pipeline-btn']"));
        Assert.DoesNotContain(cut.FindAll("h2"), h => h.TextContent.Trim() == "Live output");
    }

    /// <summary>
    /// A conflict-restarted Implementation run can be re-dispatched, and the button uses an app button
    /// style (it used the undefined .btn-primary class and rendered as a bare browser button).
    /// </summary>
    [Fact]
    public void ReDispatch_ConflictRestartRun_ShowsStyledReDispatchButton()
    {
        var summary = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "3019",
            IssueTitle = "Restarted run",
            FinalStep = PipelineStep.ConflictRestart,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
        };
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        var button = cut.Find("[data-testid='redispatch-btn']");
        Assert.Contains("btn-trigger", button.ClassList);
        button.Click();
        Assert.Contains("btn-save", cut.Find("[data-testid='redispatch-confirm-btn']").ClassList);
    }

}
