using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the PipelineSidebar component.
/// </summary>
// TODO: Add test assertions for remaining icon substitutions (phase state icons, step state icons for Completed/Active/Failed/Cancelled, quality gate pass/fail icons, chevron toggles) to catch misspelled icon names that would silently render empty SVGs.
public class PipelineSidebarComponentTests : BunitContext
{
    private static PipelineRun CreateRun(
        PipelineStep currentStep = PipelineStep.GeneratingCode,
        PipelineStep highWaterMark = PipelineStep.Created) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CurrentStep = currentStep,
        HighWaterMark = highWaterMark
    };

    // --- Linear progression (no retry) ---

    [Fact]
    public void LinearProgression_StepsBeforeCurrent_AreCompleted()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-completed", cut.Find("#step-Created").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-CloningRepository").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-CreatingBranch").GetAttribute("class"));
    }

    [Fact]
    public void LinearProgression_CurrentStep_IsActive()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-active", cut.Find("#step-GeneratingCode").GetAttribute("class"));
    }

    [Fact]
    public void LinearProgression_StepsAfterCurrent_ArePending()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-pending", cut.Find("#step-ReviewingCode").GetAttribute("class"));
        Assert.Contains("step-card-pending", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
        Assert.Contains("step-card-pending", cut.Find("#step-CreatingPullRequest").GetAttribute("class"));
    }

    // --- Retry scenario (HighWaterMark > CurrentStep) ---

    [Fact]
    public void RetryScenario_StepsBetweenCurrentAndHighWaterMark_AreRevisited()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-revisited", cut.Find("#step-ReviewingCode").GetAttribute("class"));
        Assert.Contains("step-card-revisited", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
    }

    [Fact]
    public void RetryScenario_RevisitedSteps_ShowRevisitedIcon()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.NotNull(cut.Find("#step-ReviewingCode .step-card-icon [data-icon=\"refresh-cw\"]"));
        Assert.NotNull(cut.Find("#step-RunningQualityGates .step-card-icon [data-icon=\"refresh-cw\"]"));
    }

    [Fact]
    public void RetryScenario_RevisitedSteps_AreAutoExpanded()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        run.CodeReviewIterationsCompleted = 1;
        run.CodeReviewIterationsTotal = 1;
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 failed" }
        };

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Revisited steps with data should have step-card-details rendered
        Assert.NotEmpty(cut.FindAll("#step-ReviewingCode .step-card-details"));
        Assert.NotEmpty(cut.FindAll("#step-RunningQualityGates .step-card-details"));
    }

    [Fact]
    public void RetryScenario_StepsBeyondHighWaterMark_ArePending()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-pending", cut.Find("#step-CreatingPullRequest").GetAttribute("class"));
    }

    [Fact]
    public void RetryScenario_CurrentStep_RemainsActive()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-active", cut.Find("#step-GeneratingCode").GetAttribute("class"));
    }

    [Fact]
    public void RetryScenario_StepsBeforeCurrent_AreCompleted()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.RunningQualityGates);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-completed", cut.Find("#step-Created").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-CreatingBranch").GetAttribute("class"));
    }

    // --- Terminal states unchanged ---

    [Fact]
    public void FailedState_UsesGetLastReachedStep_NotHighWaterMark()
    {
        var run = CreateRun(PipelineStep.Failed, PipelineStep.RunningQualityGates);
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("step-card-failed", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
    }

    [Fact]
    public void CompletedState_AllWorkflowSteps_AreCompleted()
    {
        var run = CreateRun(PipelineStep.Completed, PipelineStep.Completed);
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("step-card-completed", cut.Find("#step-GeneratingCode").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-PreparingForPullRequest").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-CreatingPullRequest").GetAttribute("class"));
    }

    // --- PreparingForPullRequest step ---

    [Fact]
    public void FailedDuringCleanup_MarksPreparingForPullRequestAsFailed()
    {
        var run = CreateRun(PipelineStep.Failed, PipelineStep.PreparingForPullRequest);
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("step-card-failed", cut.Find("#step-PreparingForPullRequest").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
    }

    [Fact]
    public void PreparingForPullRequest_ShownBetweenQualityGatesAndPullRequest()
    {
        var run = CreateRun(PipelineStep.PreparingForPullRequest, PipelineStep.PreparingForPullRequest);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("step-card-active", cut.Find("#step-PreparingForPullRequest").GetAttribute("class"));
        Assert.Contains("step-card-completed", cut.Find("#step-RunningQualityGates").GetAttribute("class"));
        Assert.Contains("step-card-pending", cut.Find("#step-CreatingPullRequest").GetAttribute("class"));
    }

    [Fact]
    public void PreparingForPullRequest_DisplaysCorrectName()
    {
        var run = CreateRun(PipelineStep.PreparingForPullRequest, PipelineStep.PreparingForPullRequest);
        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("Preparing for Pull Request", cut.Find("#step-PreparingForPullRequest .step-card-name").TextContent);
    }

    // --- AnalysisRecommendation badge rendering ---

    [Fact]
    public void AnalyzingCode_NotReady_ShowsNeedsRefinementBadge()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.AnalysisRecommendation = AnalysisGateResult.NotReady;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("Needs refinement", cut.Find("#step-AnalyzingCode").TextContent);
        Assert.NotNull(cut.Find("#step-AnalyzingCode [data-icon=\"alert-triangle\"]"));
    }

    [Fact]
    public void AnalyzingCode_WontDo_ShowsWontDoBadge()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.AnalysisRecommendation = AnalysisGateResult.WontDo;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("Won't do", cut.Find("#step-AnalyzingCode").TextContent);
        Assert.NotNull(cut.Find("#step-AnalyzingCode [data-icon=\"ban\"]"));
    }

    [Fact]
    public void AnalyzingCode_Ready_ShowsNoBadge()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.AnalysisRecommendation = AnalysisGateResult.Ready;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Empty(cut.FindAll("#step-AnalyzingCode [data-icon=\"alert-triangle\"]"));
        Assert.Empty(cut.FindAll("#step-AnalyzingCode [data-icon=\"ban\"]"));
    }

    [Fact]
    public void AnalyzingCode_NullRecommendation_ShowsNoBadge()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.AnalysisRecommendation = null;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Empty(cut.FindAll("#step-AnalyzingCode [data-icon=\"alert-triangle\"]"));
        Assert.Empty(cut.FindAll("#step-AnalyzingCode [data-icon=\"ban\"]"));
    }

    // --- Brain sync step rendering ---

    private static PipelineRun CreateBrainRun(
        PipelineStep currentStep,
        PipelineStep highWaterMark) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        CurrentStep = currentStep,
        HighWaterMark = highWaterMark,
        BrainProviderConfigId = "brain-1"
    };

    [Fact]
    public void BrainSyncPostRun_WhenActive_ShowsSyncing()
    {
        var run = CreateBrainRun(PipelineStep.SyncingBrainRepoPostRun, PipelineStep.SyncingBrainRepoPostRun);

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var stepMarkup = cut.Find("#step-SyncingBrainRepoPostRun").TextContent;
        Assert.Contains("Syncing...", stepMarkup);
        Assert.Empty(cut.FindAll("#step-SyncingBrainRepoPostRun [data-icon=\"alert-triangle\"]"));
    }

    [Fact]
    public void BrainSyncPostRun_WhenCompleted_WithSuccess_ShowsFileCount()
    {
        var run = CreateBrainRun(PipelineStep.Completed, PipelineStep.Completed);
        run.BrainUpdatesPushed = true;
        run.BrainFilesCommitted = 3;
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("3 file(s) pushed", cut.Find("#step-SyncingBrainRepoPostRun").TextContent);
    }

    [Fact]
    public void BrainSyncPostRun_WhenCompleted_WithFailure_ShowsWarning()
    {
        var run = CreateBrainRun(PipelineStep.Completed, PipelineStep.Completed);
        run.BrainUpdatesPushed = false;
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("Brain updates not persisted", cut.Find("#step-SyncingBrainRepoPostRun").TextContent);
    }

    [Fact]
    public void BrainSyncPreRun_WhenActive_ShowsSyncing()
    {
        var run = CreateBrainRun(PipelineStep.SyncingBrainRepoPreRun, PipelineStep.SyncingBrainRepoPreRun);

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        var stepMarkup = cut.Find("#step-SyncingBrainRepoPreRun").TextContent;
        Assert.Contains("Syncing...", stepMarkup);
        Assert.Empty(cut.FindAll("#step-SyncingBrainRepoPreRun [data-icon=\"alert-triangle\"]"));
    }

    [Fact]
    public void BrainSyncPreRun_WhenCompleted_WithSuccess_ShowsKnowledgeFileCount()
    {
        var run = CreateBrainRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.BrainContextLoaded = true;
        run.BrainKnowledgeFileCount = 5;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("5 knowledge files loaded", cut.Find("#step-SyncingBrainRepoPreRun").TextContent);
    }

    [Fact]
    public void BrainSyncPreRun_WhenCompleted_WithFailure_ShowsWarning()
    {
        var run = CreateBrainRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.BrainContextLoaded = false;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("Brain context unavailable", cut.Find("#step-SyncingBrainRepoPreRun").TextContent);
    }

    // --- Reflecting on Run step rendering ---

    [Fact]
    public void ReflectingOnRun_WhenActive_ShowsReflecting()
    {
        var run = CreateBrainRun(PipelineStep.ReflectingOnRun, PipelineStep.ReflectingOnRun);

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Contains("Reflecting on run...", cut.Find("#step-ReflectingOnRun").TextContent);
    }

    [Fact]
    public void ReflectingOnRun_WhenCompleted_ShowsComplete()
    {
        var run = CreateBrainRun(PipelineStep.Completed, PipelineStep.Completed);
        run.CompletedAt = DateTime.UtcNow;

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        Assert.Contains("Reflection complete", cut.Find("#step-ReflectingOnRun").TextContent);
    }

    [Fact]
    public void ReflectingOnRun_WhenNoBrainProvider_DoesNotExpand()
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CurrentStep = PipelineStep.Completed,
            HighWaterMark = PipelineStep.Completed,
            CompletedAt = DateTime.UtcNow,
            BrainProviderConfigId = null
        };

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run));

        var stepEl = cut.Find("#step-ReflectingOnRun");
        Assert.Null(stepEl.QuerySelector(".step-card-details"));
    }

    // --- Cost Breakdown section ---

    [Fact]
    public void CostBreakdown_WhenPhaseBreakdownEmpty_NotRendered()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        // PhaseBreakdown is empty by default

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        Assert.Empty(cut.FindAll(".phase-breakdown"));
    }

    [Fact]
    public void CostBreakdown_WhenPhaseBreakdownHasEntries_IsRendered()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(5000, 0.03m));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // TODO [WARNING]: This test only asserts that the `.phase-breakdown` container element exists in the DOM.
        // It does not verify that the collapsible header displays "Cost Breakdown" text or that any phase row
        // is actually rendered inside the component. A collapsible rendered completely empty would still pass
        // this assertion. Strengthen the test to also assert on the header text content and/or that tbody
        // contains at least one row after expanding, to confirm phase data is actually displayed.
        Assert.NotEmpty(cut.FindAll(".phase-breakdown"));
    }

    [Fact]
    public void CostBreakdown_RendersPhaseRows_WithFormattedValues()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(5000, 0.03m));
        run.Metrics.PhaseBreakdown.TryAdd("codegen", new PhaseUsage(10000, 0.08m));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Expand the collapsible to make the body visible
        cut.Find(".phase-breakdown-header").Click();

        var rows = cut.FindAll(".phase-breakdown tbody tr");
        Assert.Equal(2, rows.Count);

        // Extract all text from the table rows
        var allText = cut.Find(".phase-breakdown tbody").TextContent;
        Assert.Contains("Analysis", allText);
        Assert.Contains("Codegen", allText);
        // TODO [WARNING]: These token/cost string assertions are locale-sensitive. `CostFormatter.FormatTokens(5000)`
        // producing "5.0K" and `CostFormatter.FormatCost(0.03m)` producing "$0.03" assumes en-US locale formatting.
        // If run in a non-en-US CI environment the decimal separator or currency symbol may differ, causing a failure
        // that has nothing to do with the component logic. Fix: either pin the test thread culture to
        // CultureInfo.InvariantCulture / "en-US" in the test setup, or call `CostFormatter.FormatTokens(5000)` /
        // `CostFormatter.FormatCost(0.03m)` directly and assert equality with the actual formatter output.
        Assert.Contains("5.0K", allText);
        Assert.Contains("10.0K", allText);
        Assert.Contains("$0.03", allText);
        Assert.Contains("$0.08", allText);
    }

    [Fact]
    public void CostBreakdown_IsSortedByCostDescending()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(1000, 0.01m));
        run.Metrics.PhaseBreakdown.TryAdd("codegen", new PhaseUsage(8000, 0.05m));
        run.Metrics.PhaseBreakdown.TryAdd("review_Correctness", new PhaseUsage(3000, 0.03m));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        cut.Find(".phase-breakdown-header").Click();

        var rows = cut.FindAll(".phase-breakdown tbody tr");
        Assert.Equal(3, rows.Count);

        // Rows should be ordered: codegen ($0.05), Review: Correctness ($0.03), analysis ($0.01)
        Assert.Contains("Codegen", rows[0].TextContent);
        Assert.Contains("Review: Correctness", rows[1].TextContent);
        Assert.Contains("Analysis", rows[2].TextContent);
    }

    [Fact]
    public void CostBreakdown_WhenCostIsNull_SortsByTokensDescendingAndDisplaysDash()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(2000, null));
        run.Metrics.PhaseBreakdown.TryAdd("codegen", new PhaseUsage(8000, null));
        run.Metrics.PhaseBreakdown.TryAdd("reflection", new PhaseUsage(500, null));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        cut.Find(".phase-breakdown-header").Click();

        var rows = cut.FindAll(".phase-breakdown tbody tr");
        Assert.Equal(3, rows.Count);

        // When cost is null, secondary sort is by tokens descending: codegen (8000), analysis (2000), reflection (500)
        Assert.Contains("Codegen", rows[0].TextContent);
        Assert.Contains("Analysis", rows[1].TextContent);
        Assert.Contains("Reflection", rows[2].TextContent);

        // All cost cells should display "—" for null costs
        var allText = cut.Find(".phase-breakdown tbody").TextContent;
        Assert.DoesNotContain("$", allText);
        // TODO [WARNING]: This assertion hard-codes the em-dash character (U+2014) as the expected output of
        // `CostFormatter.FormatCost(null)`. If FormatCost returns a different character (e.g. en-dash U+2013,
        // hyphen-minus U+002D, or the literal string "N/A") this assertion will silently pass on wrong output or
        // fail unexpectedly. Fix: call `CostFormatter.FormatCost(null)` directly and assert
        // `Assert.Contains(CostFormatter.FormatCost(null), allText)` to decouple from the assumed output character.
        Assert.Contains("—", allText);
    }

    [Fact]
    public void CostBreakdown_IsCollapsedByDefault_CanBeExpanded()
    {
        var run = CreateRun(PipelineStep.GeneratingCode, PipelineStep.GeneratingCode);
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(5000, 0.03m));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Collapsed by default — body is not in DOM
        Assert.Empty(cut.FindAll(".phase-breakdown-body"));

        // Click header to expand
        cut.Find(".phase-breakdown-header").Click();

        // Body is now in DOM
        // TODO [WARNING]: This assertion only confirms that the `.phase-breakdown-body` element exists in the DOM
        // after clicking, but does not verify that the phase row is rendered inside the body. An empty body element
        // would satisfy this assertion. Strengthen by also asserting that the expanded body contains at least one
        // `tbody tr` row, confirming that phase data is actually displayed after expansion.
        Assert.NotEmpty(cut.FindAll(".phase-breakdown-body"));
    }

    [Fact]
    public async Task CostBreakdown_ExpandedStatePreservedAcrossRerender()
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode
        };
        run.Metrics.PhaseBreakdown.TryAdd("analysis", new PhaseUsage(5000, 0.03m));

        var cut = Render<PipelineSidebar>(p => p.Add(s => s.Run, run).Add(s => s.IsRunning, true));

        // Expand the collapsible — this sets _costBreakdownExpanded = true on the component
        cut.Find(".phase-breakdown-header").Click();
        Assert.NotEmpty(cut.FindAll(".phase-breakdown-body"));

        // Simulate a live update: add a new phase to the same PipelineRun object and force re-render.
        // In production, StateHasChanged is called on the parent (AgentMonitoring.razor) which passes
        // the same PipelineRun reference. Here we trigger StateHasChanged directly on the component
        // via reflection (it is protected on ComponentBase).
        run.Metrics.PhaseBreakdown.TryAdd("codegen", new PhaseUsage(10000, 0.08m));
        run.CurrentStep = PipelineStep.ReviewingCode;

        var stateHasChangedMethod = typeof(Microsoft.AspNetCore.Components.ComponentBase)
            // TODO [WARNING]: This uses private reflection into Blazor's ComponentBase to trigger StateHasChanged.
            // If the Blazor SDK renames, moves, or inlines this method the `!` null-forgiving operator will
            // cause a NullReferenceException rather than a clear test failure (no diagnostic pointing to this line).
            // Replace with the official bUnit API: `cut.SetParametersAndRender(p => p.Add(s => s.Run, run))`
            // to achieve the same re-render without coupling to Blazor framework internals.
            .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => stateHasChangedMethod.Invoke(cut.Instance, null));

        // Body should still be visible — expanded state preserved via _costBreakdownExpanded field
        Assert.NotEmpty(cut.FindAll(".phase-breakdown-body"));
        // Both phases should now be rendered
        var allText = cut.Find(".phase-breakdown tbody").TextContent;
        Assert.Contains("Analysis", allText);
        Assert.Contains("Codegen", allText);
    }

    // TODO [WARNING]: Missing test — PhaseBreakdown transitions from empty to populated during a live re-render.
    // The existing CostBreakdown_ExpandedStatePreservedAcrossRerender test starts with a pre-populated entry.
    // A test that renders PipelineSidebar with an empty PhaseBreakdown (Cost Breakdown hidden), then mutates
    // PhaseBreakdown to add an entry and triggers a re-render, should assert that the Cost Breakdown section
    // appears after the update. This covers the initial-appearance edge case for the @if guard condition.

    // TODO [WARNING]: Missing test — _costBreakdownExpanded is reset to false when Run.RunId changes.
    // OnParametersSet resets _costBreakdownExpanded when _lastRunId != Run.RunId (a new run replaces the current one).
    // Add a test that: (1) renders with run A (populated breakdown, expand it), (2) calls SetParametersAndRender
    // with run B (a different RunId), and asserts that the cost breakdown collapsible is collapsed (body absent)
    // for the new run. This verifies the reset logic in OnParametersSet.
}

