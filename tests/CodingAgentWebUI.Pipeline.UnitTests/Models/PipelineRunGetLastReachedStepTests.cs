using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineRunGetLastReachedStepTests
{
    private static PipelineRun CreateRun(PipelineStep highWaterMark = PipelineStep.Created) => new()
    {
        RunId = "run-1",
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow,
        CurrentStep = PipelineStep.Failed,
        HighWaterMark = highWaterMark
    };

    [Fact]
    public void ReturnsCreated_WhenNoProgressMade()
    {
        var run = CreateRun();
        run.GetLastReachedStep().Should().Be(PipelineStep.Created);
    }

    [Fact]
    public void ReturnsCloningRepository_WhenWorkspaceSet()
    {
        var run = CreateRun(PipelineStep.CloningRepository);
        run.WorkspacePath = "/tmp/workspace";
        run.GetLastReachedStep().Should().Be(PipelineStep.CloningRepository);
    }

    [Fact]
    public void ReturnsSyncingBrainRepoPreRun_WhenHighWaterMarkReachesBrainSync()
    {
        var run = CreateRun(PipelineStep.SyncingBrainRepoPreRun);
        run.WorkspacePath = "/tmp/workspace";
        run.GetLastReachedStep().Should().Be(PipelineStep.SyncingBrainRepoPreRun);
    }

    [Fact]
    public void ReturnsCloningRepository_WhenHighWaterMarkBelowBrainSync()
    {
        var run = CreateRun(PipelineStep.CloningRepository);
        run.WorkspacePath = "/tmp/workspace";
        run.GetLastReachedStep().Should().Be(PipelineStep.CloningRepository);
    }

    [Fact]
    public void ReturnsCreatingBranch_WhenBranchNameSet()
    {
        var run = CreateRun(PipelineStep.CreatingBranch);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.GetLastReachedStep().Should().Be(PipelineStep.CreatingBranch);
    }

    [Fact]
    public void ReturnsAnalyzingCode_WhenHighWaterMarkReachesAnalyzing_AndAnalysisContentNull()
    {
        var run = CreateRun(PipelineStep.AnalyzingCode);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.AnalysisContent = null; // cleared during retry
        run.GetLastReachedStep().Should().Be(PipelineStep.AnalyzingCode);
    }

    [Fact]
    public void ReturnsCreatingBranch_WhenHighWaterMarkBelowAnalyzing()
    {
        var run = CreateRun(PipelineStep.CreatingBranch);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.AnalysisContent = null;
        run.GetLastReachedStep().Should().Be(PipelineStep.CreatingBranch);
    }

    [Fact]
    public void ReturnsPostingAnalysis_WhenAnalysisContentSet()
    {
        var run = CreateRun(PipelineStep.PostingAnalysis);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.AnalysisContent = "some analysis";
        run.GetLastReachedStep().Should().Be(PipelineStep.PostingAnalysis);
    }

    [Fact]
    public void ReturnsGeneratingCode_WhenFilesChanged()
    {
        var run = CreateRun(PipelineStep.GeneratingCode);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.FilesChangedCount = 3;
        run.GetLastReachedStep().Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public void ReturnsRunningQualityGates_WhenQualityReportExists()
    {
        var run = CreateRun(PipelineStep.RunningQualityGates);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.FilesChangedCount = 3;
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };
        run.GetLastReachedStep().Should().Be(PipelineStep.RunningQualityGates);
    }

    [Fact]
    public void ReturnsPreparingForPullRequest_WhenHighWaterMarkAndQualityReport()
    {
        var run = CreateRun(PipelineStep.PreparingForPullRequest);
        run.WorkspacePath = "/tmp/workspace";
        run.BranchName = "feature/test";
        run.FilesChangedCount = 3;
        run.LatestQualityReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };
        run.GetLastReachedStep().Should().Be(PipelineStep.PreparingForPullRequest);
    }

    [Fact]
    public void ReturnsCreatingPullRequest_WhenPullRequestUrlSet()
    {
        var run = CreateRun(PipelineStep.CreatingPullRequest);
        run.PullRequestUrl = "https://github.com/org/repo/pull/1";
        run.GetLastReachedStep().Should().Be(PipelineStep.CreatingPullRequest);
    }
}
