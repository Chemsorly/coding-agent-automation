using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Models;
using StackExchange.Redis;

namespace CodingAgentWebUI.Orchestration.UnitTests.Redis;

/// <summary>
/// Property-like roundtrip tests for <see cref="PipelineRunHashExtensions"/>.
///
/// Validates that <c>FromHash(run.ToHashEntries())</c> reconstructs the PipelineRun
/// with equal scalar and string fields. Guards against silent field-drop bugs — the
/// same failure class as MessagePack key gaps, already covered by
/// <c>SignalRMessageRoundtripPropertyTests</c> for the agent side.
///
/// Documented lossiness (by design, not a bug):
/// - Empty strings serialise as "" and deserialise back as null via NullIfEmpty().
///   Tests use non-empty values to distinguish null-intent from empty-string-intent.
/// - Queue fields (OutputLines, ChatHistory) are not serialised — not asserted here.
/// </summary>
public class PipelineRunHashExtensionsPropertyTests
{
    private static PipelineRun MakeMinimalRun(string runId = "test-run-1") =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("org/repo#42"),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
        });

    // ── Core roundtrip — required fields ─────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesRequiredScalars()
    {
        var run = MakeMinimalRun("run-core");
        var hash = run.ToHashEntries();

        var restored = PipelineRunHashExtensions.FromHash(hash);

        restored.Should().NotBeNull();
        restored!.RunId.Should().Be(run.RunId);
        restored.IssueIdentifier.Should().Be(run.IssueIdentifier);
        restored.IssueProviderConfigId.Should().Be(run.IssueProviderConfigId);
        restored.RepoProviderConfigId.Should().Be(run.RepoProviderConfigId);
        restored.InitiatedBy.Should().Be(run.InitiatedBy);
        restored.RunType.Should().Be(run.RunType);
    }

    // ── Nullable string fields ────────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesNullableStrings()
    {
        var run = MakeMinimalRun();
        run.BranchName     = "feature/test-branch";
        run.AgentId        = "agent-xyz";
        run.ModelName      = "claude-sonnet-4";
        run.FailureReason  = "timeout";
        run.PullRequestUrl = "https://github.com/org/repo/pull/99";
        run.RepositoryName = "org/repo";
        run.WorkspacePath  = "/tmp/ws/test-run";
        run.ProjectId      = "proj-abc";
        run.ProjectName    = "My Project";
        run.FinalLabel     = "agent:done";

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.BranchName.Should().Be(run.BranchName);
        restored.AgentId.Should().Be(run.AgentId);
        restored.ModelName.Should().Be(run.ModelName);
        restored.FailureReason.Should().Be(run.FailureReason);
        restored.PullRequestUrl.Should().Be(run.PullRequestUrl);
        restored.RepositoryName.Should().Be(run.RepositoryName);
        restored.WorkspacePath.Should().Be(run.WorkspacePath);
        restored.ProjectId.Should().Be(run.ProjectId);
        restored.ProjectName.Should().Be(run.ProjectName);
        restored.FinalLabel.Should().Be(run.FinalLabel);
    }

    // ── Integer counters ──────────────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesIntegerFields()
    {
        var run = MakeMinimalRun();
        run.FilesChangedCount = 17;
        run.LinesAdded        = 342;
        run.LinesRemoved      = 89;
        run.RetryCount        = 2;
        run.InfrastructureRetryCount = 1;
        run.InlineCommentsPosted     = 5;
        run.OpenIssuesDownloaded     = 30;
        run.SetCodeReviewCounts(critical: 3, warning: 7, suggestion: 12);

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.FilesChangedCount.Should().Be(17);
        restored.LinesAdded.Should().Be(342);
        restored.LinesRemoved.Should().Be(89);
        restored.RetryCount.Should().Be(2);
        restored.InfrastructureRetryCount.Should().Be(1);
        restored.InlineCommentsPosted.Should().Be(5);
        restored.OpenIssuesDownloaded.Should().Be(30);
        restored.CodeReviewCriticalCount.Should().Be(3);
        restored.CodeReviewWarningCount.Should().Be(7);
        restored.CodeReviewSuggestionCount.Should().Be(12);
    }

    // ── Long / decimal / boolean fields ──────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesLongsDecimalsBooleans()
    {
        var run = MakeMinimalRun();
        run.TotalTokens       = 123_456_789L;
        run.CacheReadTokens   = 50_000L;
        run.CacheWriteTokens  = 25_000L;
        run.TotalCost         = 1.2345m;
        run.BrainContextLoaded    = true;
        run.BrainUpdatesPushed    = true;
        run.IsDraftPr             = true;
        run.AnalysisSkipped       = false;
        run.MergeForceResolved    = true;
        run.InlineCommentsDegraded = true;
        run.BaselineHealthPassed  = true;

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.TotalTokens.Should().Be(123_456_789L);
        restored.CacheReadTokens.Should().Be(50_000L);
        restored.CacheWriteTokens.Should().Be(25_000L);
        restored.TotalCost.Should().Be(1.2345m);
        restored.BrainContextLoaded.Should().BeTrue();
        restored.BrainUpdatesPushed.Should().BeTrue();
        restored.IsDraftPr.Should().BeTrue();
        restored.AnalysisSkipped.Should().BeFalse();
        restored.MergeForceResolved.Should().BeTrue();
        restored.InlineCommentsDegraded.Should().BeTrue();
        restored.BaselineHealthPassed.Should().BeTrue();
    }

    // ── Enum fields ───────────────────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesRunType()
    {
        foreach (var runType in Enum.GetValues<PipelineRunType>())
        {
            var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = $"run-{runType}",
                IssueIdentifier = new IssueIdentifier("org/repo#1"),
                IssueTitle = "t",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                RunType = runType
            });

            var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;
            restored.RunType.Should().Be(runType, $"RunType.{runType} must roundtrip correctly");
        }
    }

    // ── Pipeline step fields ──────────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesCurrentStepAndHighWaterMark()
    {
        var run = MakeMinimalRun();
        run.CurrentStep = PipelineStep.GeneratingCode;
        run.HighWaterMark = PipelineStep.RunningQualityGates;

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        restored.HighWaterMark.Should().Be(PipelineStep.RunningQualityGates);
    }

    // ── Null fields survive as null ───────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_NullOptionalFields_SurviveAsNull()
    {
        var run = MakeMinimalRun();
        // All optional nullable strings are null by default

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.BranchName.Should().BeNull();
        restored.AgentId.Should().BeNull();
        restored.ModelName.Should().BeNull();
        restored.FailureReason.Should().BeNull();
        restored.TotalCost.Should().BeNull();
        restored.BaselineHealthPassed.Should().BeNull();
    }

    // ── Corrupt / empty hash returns null ────────────────────────────────────

    [Fact]
    public void FromHash_EmptyArray_ReturnsNull()
    {
        var result = PipelineRunHashExtensions.FromHash(Array.Empty<HashEntry>());
        result.Should().BeNull("empty hash must return null");
    }

    [Fact]
    public void FromHash_MissingRequiredField_ReturnsNull()
    {
        // Hash with everything except runId → partial/corrupt hash
        var run = MakeMinimalRun();
        var hash = run.ToHashEntries()
            .Where(e => (string)e.Name! != "runId")
            .ToArray();

        var result = PipelineRunHashExtensions.FromHash(hash);
        result.Should().BeNull("missing required runId must return null");
    }

    // ── Multi-field roundtrip (integration-style) ─────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_FullyPopulatedRun_PreservesAllAssertedFields()
    {
        var run = MakeMinimalRun("run-full");
        run.BranchName                     = "feature/full-roundtrip";
        run.AgentId                        = "agent-full";
        run.ModelName                      = "gpt-4o";
        run.PullRequestUrl                 = "https://github.com/org/repo/pull/1";
        run.PullRequestBody                = "PR body text";
        run.PullRequestNumber              = "1";
        run.FilesChangedCount              = 42;
        run.LinesAdded                     = 1000;
        run.LinesRemoved                   = 200;
        run.TotalTokens                    = 500_000L;
        run.TotalCost                      = 3.14m;
        run.BrainContextLoaded             = true;
        run.IsDraftPr                      = false;
        run.CurrentStep                    = PipelineStep.Completed;
        run.HighWaterMark                  = PipelineStep.Completed;
        run.SetCodeReviewCounts(1, 2, 3);
        run.IssueLabels                    = ["bug", "enhancement"];
        run.CodeReviewAgentsRun            = ["agent-1", "agent-2"];

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        // Scalar fields
        restored.RunId.Should().Be("run-full");
        restored.BranchName.Should().Be("feature/full-roundtrip");
        restored.AgentId.Should().Be("agent-full");
        restored.ModelName.Should().Be("gpt-4o");
        restored.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/1");
        restored.FilesChangedCount.Should().Be(42);
        restored.LinesAdded.Should().Be(1000);
        restored.LinesRemoved.Should().Be(200);
        restored.TotalTokens.Should().Be(500_000L);
        restored.TotalCost.Should().Be(3.14m);
        restored.BrainContextLoaded.Should().BeTrue();
        restored.IsDraftPr.Should().BeFalse();
        restored.CurrentStep.Should().Be(PipelineStep.Completed);
        restored.HighWaterMark.Should().Be(PipelineStep.Completed);
        restored.CodeReviewCriticalCount.Should().Be(1);
        restored.CodeReviewWarningCount.Should().Be(2);
        restored.CodeReviewSuggestionCount.Should().Be(3);

        // JSON sub-object collections
        restored.IssueLabels.Should().BeEquivalentTo(new[] { "bug", "enhancement" });
        restored.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "agent-1", "agent-2" });
    }
}
