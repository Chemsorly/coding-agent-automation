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
/// </summary>
public class RunPageComponentTests : BunitContext
{
    // ── Shared scaffolding ────────────────────────────────────────────────

    /// <summary>
    /// Registers all services required by RunPage into bUnit's service collection,
    /// configured so that GetRunAsync returns the provided summary and the page renders
    /// in a completed (non-live) state.
    /// </summary>
    private void RegisterServices(PipelineRunSummary summary)
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

        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();

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
}
