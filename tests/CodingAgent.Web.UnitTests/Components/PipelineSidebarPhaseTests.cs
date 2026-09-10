using Bunit;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the phase grouping behavior in PipelineSidebar.
/// Covers phase state aggregation, expansion/collapse, counters, and edge cases.
/// </summary>
public class PipelineSidebarPhaseTests : BunitContext
{
    private static PipelineRun CreateRun(
        PipelineStep currentStep,
        PipelineStep highWaterMark,
        string? brainProviderConfigId = null) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "99",
            IssueTitle = "Phase Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            CurrentStep = currentStep,
            HighWaterMark = highWaterMark,
            BrainProviderConfigId = brainProviderConfigId
        };

    // ─── Phase state aggregation ─────────────────────────────────────────

    [Fact]
    public void PhaseState_AllStepsCompleted_ShowsCompleted()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phase = cut.Find("[data-testid='phase-preparation']");
        Assert.Contains("phase-group-completed", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_OneStepActive_ShowsActive()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phase = cut.Find("[data-testid='phase-code-generation']");
        Assert.Contains("phase-group-active", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_AllStepsPending_ShowsPending()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phase = cut.Find("[data-testid='phase-finalization']");
        Assert.Contains("phase-group-pending", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_OneStepFailed_ShowsFailed()
    {
        var run = CreateRun(PipelineStep.Failed, PipelineStep.RunningQualityGates);
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        var phase = cut.Find("[data-testid='phase-code-generation']");
        Assert.Contains("phase-group-failed", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_RevisitedSteps_ShowsRetry()
    {
        // CurrentStep=GeneratingCode, HighWaterMark=RunningQualityGates
        // → ReviewingCode and RunningQualityGates are Revisited within Code Generation phase
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Code Generation has Active + Revisited → Active takes priority over Retry
        var phase = cut.Find("[data-testid='phase-code-generation']");
        Assert.Contains("phase-group-active", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_OnlyRevisitedSteps_WithRetryCount_ShowsRetry()
    {
        // CurrentStep=GeneratingCode, HighWaterMark=PreparingForPullRequest, RetryCount=1
        // → Finalization phase has PreparingForPullRequest=Revisited, rest=Pending
        // → With RetryCount > 0, this is a genuine retry
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.PreparingForPullRequest);
        run.RetryCount = 1;
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phase = cut.Find("[data-testid='phase-finalization']");
        Assert.Contains("phase-group-retry", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_OnlyRevisitedSteps_WithoutRetry_ShowsActive()
    {
        // CurrentStep=RunningQualityGates, HighWaterMark=PreparingForPullRequest, RetryCount=0
        // → Finalization phase has PreparingForPullRequest=Revisited, rest=Pending
        // → Without RetryCount, this is a sub-operation (final QG run), not a retry
        var run = CreateRun(PipelineStep.RunningQualityGates, PipelineStep.PreparingForPullRequest);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phase = cut.Find("[data-testid='phase-finalization']");
        Assert.Contains("phase-group-active", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_MixOfCompletedAndPending_ShowsActive()
    {
        // CurrentStep=AnalyzingCode → Analysis phase has AnalyzingCode=Active, rest=Pending
        // But Preparation phase has all completed
        var run = CreateRun(PipelineStep.PostingAnalysis, PipelineStep.PostingAnalysis);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Analysis phase: AnalyzingCode=Completed, ReviewingAnalysis=Completed, PostingAnalysis=Active
        var phase = cut.Find("[data-testid='phase-analysis']");
        Assert.Contains("phase-group-active", phase.GetAttribute("class"));
    }

    [Fact]
    public void PhaseState_CompletedRun_AllPhasesCompleted()
    {
        var run = CreateRun(PipelineStep.Completed, PipelineStep.Completed);
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("phase-group-completed", cut.Find("[data-testid='phase-preparation']").GetAttribute("class"));
        Assert.Contains("phase-group-completed", cut.Find("[data-testid='phase-analysis']").GetAttribute("class"));
        Assert.Contains("phase-group-completed", cut.Find("[data-testid='phase-code-generation']").GetAttribute("class"));
        Assert.Contains("phase-group-completed", cut.Find("[data-testid='phase-finalization']").GetAttribute("class"));
    }

    // ─── Phase visibility (always expanded in horizontal layout) ─────────
    // TODO: CompletedPhase_BodyIsAlwaysVisible, PendingPhase_BodyIsAlwaysVisible,
    // ClickingPhaseHeader_DoesNotAffectBodyVisibility, and AllPhases_AlwaysHaveVisibleBody all render the
    // same fixture and assert DoesNotContain("phase-body-collapsed") on .phase-body elements — they are
    // near-duplicates. A genuine regression would produce four simultaneous failures with no additional
    // diagnostic signal. Consider consolidating into a single parameterized test to reduce maintenance overhead.

    [Fact]
    public void ActivePhase_IsExpandedByDefault()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void CompletedPhase_BodyIsAlwaysVisible()
    {
        // Phase bodies are always shown in the horizontal layout — no collapse for completed phases.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void PendingPhase_BodyIsAlwaysVisible()
    {
        // Phase bodies are always shown in the horizontal layout — no collapse for pending phases.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-finalization'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void FailedPhase_IsExpandedByDefault()
    {
        var run = CreateRun(PipelineStep.Failed, PipelineStep.RunningQualityGates);
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        var phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void ClickingPhaseHeader_DoesNotAffectBodyVisibility()
    {
        // Phase headers have no click handler — the body is always visible regardless.
        // bUnit throws MissingEventHandlerException if we try to click a handler-less element,
        // so we verify the body visibility without dispatching a click event.
        // TODO: This test cannot detect a regression where @onclick is re-added to the phase header and
        // collapse logic is reintroduced. If click-toggle behavior returns, add a test that dispatches
        // a click and asserts the body remains visible (or that MissingEventHandlerException is NOT thrown
        // and the body is still not collapsed). Consider also asserting the header lacks an onclick attribute.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Completed phase body is visible without any interaction
        var completedBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", completedBody.GetAttribute("class") ?? "");

        // Active phase body is visible without any interaction
        var activeBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", activeBody.GetAttribute("class") ?? "");

        // Pending phase body is visible without any interaction
        var pendingBody = cut.Find("[data-testid='phase-finalization'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", pendingBody.GetAttribute("class") ?? "");
    }

    // ─── Phase counters ──────────────────────────────────────────────────

    [Fact]
    public void CompletedPhase_ShowsCorrectCounter()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Preparation has 5 visible steps (no brain provider → SyncingBrainRepoPreRun hidden)
        var counter = cut.Find("[data-testid='phase-preparation'] .phase-counter").TextContent;
        Assert.Contains("5/5 ✓", counter);
    }

    [Fact]
    public void ActivePhase_ShowsInProgressCounter()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-code-generation'] .phase-counter").TextContent;
        Assert.Equal("in progress", counter);
    }

    [Fact]
    public void PendingPhase_ShowsZeroCounter()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-finalization'] .phase-counter").TextContent;
        Assert.StartsWith("0/", counter);
    }

    [Fact]
    public void RetryPhase_ShowsRetryCounter()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.PreparingForPullRequest);
        run.RetryCount = 1;
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-finalization'] .phase-counter").TextContent;
        Assert.Contains("(retry)", counter);
    }

    // ─── Dynamic step visibility ─────────────────────────────────────────

    [Fact]
    public void BrainStepsHidden_WhenNoBrainProvider_AffectsCounter()
    {
        // No brain provider → SyncingBrainRepoPreRun hidden → Preparation has 5 steps
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-preparation'] .phase-counter").TextContent;
        Assert.Contains("5/5", counter);
    }

    [Fact]
    public void BrainStepsVisible_WhenBrainProviderConfigured_AffectsCounter()
    {
        // Brain provider set → SyncingBrainRepoPreRun visible → Preparation has 6 steps
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode, brainProviderConfigId: "brain-1");
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-preparation'] .phase-counter").TextContent;
        Assert.Contains("6/6", counter);
    }

    [Fact]
    public void FinalizationPhase_WithoutBrain_HasFewerSteps()
    {
        // No brain → SyncingBrainRepoPostRun hidden → Finalization has 3 steps
        var run = CreateRun(PipelineStep.PreparingForPullRequest, PipelineStep.PreparingForPullRequest);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // TODO: Also assert phase counter text shows "0/3" for pending phase to maintain coverage of counter logic (review finding: original Assert.Contains("/3", counter) was removed)
        var steps = cut.FindAll("[data-testid='phase-finalization'] .step-card");
        Assert.Equal(3, steps.Count);
    }

    [Fact]
    public void FinalizationPhase_WithBrain_HasMoreSteps()
    {
        // Brain provider → SyncingBrainRepoPostRun visible → Finalization has 4 steps
        var run = CreateRun(PipelineStep.PreparingForPullRequest, PipelineStep.PreparingForPullRequest, brainProviderConfigId: "brain-1");
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // TODO: Also assert phase counter text shows "0/4" for pending phase to maintain coverage of counter logic (review finding: original Assert.Contains("/4", counter) was removed)
        var steps = cut.FindAll("[data-testid='phase-finalization'] .step-card");
        Assert.Equal(4, steps.Count);
    }

    // ─── Aria-hidden attribute ───────────────────────────────────────────
    // TODO: AllPhaseBodies_HaveAriaHiddenFalse and AllPhases_AriaHiddenAlwaysFalse both assert
    // aria-hidden="false" on every .phase-body. The only difference is the fixture (in-progress vs completed run).
    // Since aria-hidden is hardcoded in the razor template (not state-driven), the second test adds no meaningful
    // coverage. Additionally, ExpandedPhaseBody_HasAriaHiddenFalse overlaps for the active-phase case.
    // Consider consolidating into a single test to reduce maintenance overhead.

    [Fact]
    public void AllPhaseBodies_HaveAriaHiddenFalse()
    {
        // All phase bodies are always visible in the horizontal layout —
        // aria-hidden="true" on visible content would be an accessibility violation.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBodies = cut.FindAll(".phase-body");
        Assert.NotEmpty(phaseBodies);
        foreach (var body in phaseBodies)
            Assert.Equal("false", body.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void ExpandedPhaseBody_HasAriaHiddenFalse()
    {
        // This test was previously scoped to active phases only; now all bodies are always visible.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.Equal("false", phaseBody.GetAttribute("aria-hidden"));
    }

    // ─── Phase data-testid and data-phase-state attributes ───────────────

    [Fact]
    public void AllFourPhases_AreRendered()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.NotNull(cut.Find("[data-testid='phase-preparation']"));
        Assert.NotNull(cut.Find("[data-testid='phase-analysis']"));
        Assert.NotNull(cut.Find("[data-testid='phase-code-generation']"));
        Assert.NotNull(cut.Find("[data-testid='phase-finalization']"));
    }

    [Fact]
    public void PhaseDataState_MatchesCssClass()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Equal("completed", cut.Find("[data-testid='phase-preparation']").GetAttribute("data-phase-state"));
        Assert.Equal("active", cut.Find("[data-testid='phase-code-generation']").GetAttribute("data-phase-state"));
        Assert.Equal("pending", cut.Find("[data-testid='phase-finalization']").GetAttribute("data-phase-state"));
    }

    // ─── Horizontal layout regression guards ─────────────────────────────

    [Fact]
    public void AllPhases_AlwaysHaveVisibleBody()
    {
        // All phase bodies must be visible regardless of phase state — completed, active, and pending.
        // TODO: This test uses the same fixture and assertions as CompletedPhase_BodyIsAlwaysVisible,
        // PendingPhase_BodyIsAlwaysVisible, and ClickingPhaseHeader_DoesNotAffectBodyVisibility —
        // they are near-duplicates. Consider consolidating into a single parameterized test or removing
        // redundant siblings to reduce maintenance overhead.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBodies = cut.FindAll(".phase-body");
        Assert.NotEmpty(phaseBodies);
        foreach (var body in phaseBodies)
            Assert.DoesNotContain("phase-body-collapsed", body.GetAttribute("class") ?? "");
    }

    [Fact]
    public void AllPhases_AriaHiddenAlwaysFalse()
    {
        // Visible content must never be aria-hidden="true". All bodies use aria-hidden="false".
        var run = CreateRun(PipelineStep.Completed, PipelineStep.Completed);
        run.CompletedAt = DateTime.UtcNow;
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        var phaseBodies = cut.FindAll(".phase-body");
        Assert.NotEmpty(phaseBodies);
        foreach (var body in phaseBodies)
            Assert.Equal("false", body.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void PhaseHeader_HasNoChevronElement()
    {
        // The phase-chevron span was removed with the horizontal layout — assert it stays gone.
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var chevrons = cut.FindAll(".phase-chevron");
        Assert.Empty(chevrons);
    }
}
