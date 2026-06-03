using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

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

    // ─── Phase expansion/collapse ────────────────────────────────────────

    [Fact]
    public void ActivePhase_IsExpandedByDefault()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void CompletedPhase_IsCollapsedByDefault()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.Contains("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void PendingPhase_IsCollapsedByDefault()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-finalization'] .phase-body");
        Assert.Contains("phase-body-collapsed", phaseBody.GetAttribute("class"));
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
    public void ClickingCompletedPhaseHeader_ExpandsIt()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Initially collapsed
        var phaseBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.Contains("phase-body-collapsed", phaseBody.GetAttribute("class"));

        // Click the header
        cut.Find("[data-testid='phase-preparation'] .phase-header").Click();

        // Now expanded
        phaseBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void ClickingActivePhaseHeader_CollapsesIt()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Initially expanded (active)
        var phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));

        // Click the header to collapse
        cut.Find("[data-testid='phase-code-generation'] .phase-header").Click();

        // Now collapsed
        phaseBody = cut.Find("[data-testid='phase-code-generation'] .phase-body");
        Assert.Contains("phase-body-collapsed", phaseBody.GetAttribute("class"));
    }

    [Fact]
    public void ClickingPendingPhaseHeader_ExpandsIt()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Initially collapsed (pending)
        var phaseBody = cut.Find("[data-testid='phase-finalization'] .phase-body");
        Assert.Contains("phase-body-collapsed", phaseBody.GetAttribute("class"));

        // Click the header
        cut.Find("[data-testid='phase-finalization'] .phase-header").Click();

        // Now expanded
        phaseBody = cut.Find("[data-testid='phase-finalization'] .phase-body");
        Assert.DoesNotContain("phase-body-collapsed", phaseBody.GetAttribute("class"));
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
        Assert.Contains("in progress", counter);
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

        var counter = cut.Find("[data-testid='phase-finalization'] .phase-counter").TextContent;
        Assert.Contains("/3", counter);
    }

    [Fact]
    public void FinalizationPhase_WithBrain_HasMoreSteps()
    {
        // Brain provider → SyncingBrainRepoPostRun visible → Finalization has 4 steps
        var run = CreateRun(PipelineStep.PreparingForPullRequest, PipelineStep.PreparingForPullRequest, brainProviderConfigId: "brain-1");
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var counter = cut.Find("[data-testid='phase-finalization'] .phase-counter").TextContent;
        Assert.Contains("/4", counter);
    }

    // ─── Aria-hidden attribute ───────────────────────────────────────────

    [Fact]
    public void CollapsedPhaseBody_HasAriaHiddenTrue()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var phaseBody = cut.Find("[data-testid='phase-preparation'] .phase-body");
        Assert.Equal("true", phaseBody.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void ExpandedPhaseBody_HasAriaHiddenFalse()
    {
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
}
