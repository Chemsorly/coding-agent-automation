using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the PipelineSidebar component.
/// </summary>
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

        Assert.Contains("🔄", cut.Find("#step-ReviewingCode .step-card-icon").TextContent);
        Assert.Contains("🔄", cut.Find("#step-RunningQualityGates .step-card-icon").TextContent);
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
        Assert.DoesNotContain("⚠️", stepMarkup);
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
        Assert.DoesNotContain("⚠️", stepMarkup);
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
}

