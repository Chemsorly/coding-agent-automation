using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Tests for PipelineRunSummary mapping from PipelineRun.ToSummary().
/// </summary>
public class PipelineRunSummaryTests
{
    [Fact]
    public void RunMode_WhenLinkedPullRequestSet_IsRework()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunMode = RunMode.Rework,
            LinkedPullRequest = new LinkedPullRequest
            {
                Number = 7,
                BranchName = "feature/auto-42-test",
                Url = "https://github.com/test/repo/pull/7",
                IsDraft = false
            }
        };

        var summary = run.ToSummary();

        summary.RunMode.Should().Be(RunMode.Rework);
    }

    [Fact]
    public void ToSummary_FlattensQualityGateOutcomes_FromLatestReport()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            LatestQualityReport = new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = false }
            }
        };

        var summary = run.ToSummary();

        summary.QualityGateOutcomes.Should().NotBeNull();
        summary.QualityGateOutcomes!.Should().Contain(g => g.GateName == "Compilation" && g.Passed);
        summary.QualityGateOutcomes!.Should().Contain(g => g.GateName == "Tests" && !g.Passed);
    }

    [Fact]
    public void ToSummary_NoQualityReport_LeavesGateOutcomesNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        run.ToSummary().QualityGateOutcomes.Should().BeNull();
    }

    [Fact]
    public void ToSummary_CapturesBrainUsageFields()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            BrainContextLoaded = true,
            BrainKnowledgeFileCount = 7
        };

        var summary = run.ToSummary();

        summary.BrainContextLoaded.Should().BeTrue();
        summary.BrainKnowledgeFileCount.Should().Be(7);
    }

    [Fact]
    public void RunMode_WhenNoLinkedPullRequest_IsNew()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.RunMode.Should().Be(RunMode.New);
    }

    [Fact]
    public void AgentId_WhenSet_MapsToSummary()
    {
        // TODO: Add equivalent test for ProjectId mapping (both set and null cases) to match AgentId coverage
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-01"
        };

        var summary = run.ToSummary();

        summary.AgentId.Should().Be("agent-01");
    }

    [Fact]
    public void AgentId_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.AgentId.Should().BeNull();
    }

    [Fact]
    public void FailureReason_WhenSet_MapsToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            FailureReason = "Analysis failed after 2 attempt(s)"
        };

        var summary = run.ToSummary();

        summary.FailureReason.Should().Be("Analysis failed after 2 attempt(s)");
    }

    [Fact]
    public void FailureReason_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.FailureReason.Should().BeNull();
    }

    [Fact]
    public void ToSummary_MapsNonEmptyPhaseBreakdown()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(1500, 0.05m);
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(50000, 1.20m);

        var summary = run.ToSummary();

        summary.PhaseBreakdown.Should().NotBeNull();
        summary.PhaseBreakdown.Should().HaveCount(2);
        summary.PhaseBreakdown!["analysis"].Tokens.Should().Be(1500);
        summary.PhaseBreakdown["analysis"].Cost.Should().Be(0.05m);
        summary.PhaseBreakdown["codegen"].Tokens.Should().Be(50000);
        summary.PhaseBreakdown["codegen"].Cost.Should().Be(1.20m);
    }

    [Fact]
    public void ToSummary_MapsEmptyPhaseBreakdownToNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.PhaseBreakdown.Should().BeNull();
    }

    // TODO: Add backward-compatibility deserialization test — deserialize a JSON string representing
    // a pre-feature PipelineRunSummary (without PhaseBreakdown property) and assert PhaseBreakdown is null.
    // TODO: Add backward-compatibility deserialization test for ProjectId — deserialize a JSON payload
    // from before this fix (lacking ProjectId field) into PipelineRunSummary and assert ProjectId is null without errors.

    [Fact]
    public void ToSummary_WithFinalStepOverride_UsesOverrideInsteadOfCurrentStep()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Override test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CurrentStep = PipelineStep.RunningQualityGates;

        var summary = run.ToSummary(finalStepOverride: PipelineStep.Failed);

        summary.FinalStep.Should().Be(PipelineStep.Failed);
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates,
            "ToSummary must not mutate the caller's PipelineRun.CurrentStep");
    }

    [Fact]
    public void ToSummary_WithNullFinalStepOverride_UsesCurrentStep()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Null override test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CurrentStep = PipelineStep.Completed;

        var summary = run.ToSummary(finalStepOverride: null);

        summary.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void AgentProviderConfigId_WhenSet_MapsToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            AgentProviderConfigId = "agent-provider-cfg-01"
        };

        var summary = run.ToSummary();

        summary.AgentProviderConfigId.Should().Be("agent-provider-cfg-01");
    }

    [Fact]
    public void AgentProviderConfigId_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.AgentProviderConfigId.Should().BeNull();
    }

    [Fact]
    public void ToSummary_MapsCacheTokensFromMetrics()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Cache Token Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CacheReadTokens = 100;
        run.CacheWriteTokens = 50;

        var summary = run.ToSummary();

        summary.CacheReadTokens.Should().Be(100);
        summary.CacheWriteTokens.Should().Be(50);
    }

    // TODO: This zero-default test does not detect a regression if the `CacheReadTokens = CacheReadTokens`
    // mapping line is accidentally omitted from ToSummary() — the long default value of 0 would mask the
    // missing mapping. The non-zero mapping test (ToSummary_MapsCacheTokensFromMetrics) above already
    // catches that omission. Consider removing this test or converting it to assert a meaningful invariant.
    [Fact]
    public void ToSummary_WhenNoCacheTokens_CacheFieldsAreZero()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "No Cache Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.CacheReadTokens.Should().Be(0);
        summary.CacheWriteTokens.Should().Be(0);
    }

    /// <summary>
    /// Type-locking test: asserts that IssueIdentifier is typed as IssueIdentifier (not string).
    /// Accessing .Value only compiles if the property is an IssueIdentifier struct — this test
    /// will fail to compile if the property type reverts to string.
    /// </summary>
    // Note: The type-lock is only partially enforced — `string issueIdStr = summary.IssueIdentifier`
    // compiles whether the property is IssueIdentifier or string (implicit conversion goes both ways).
    // The real compile-time guard is `_ = summary.IssueIdentifier.Value`. Consider also adding an edge
    // case assertion for default(IssueIdentifier) / null Value to improve coverage.
    [Fact]
    public void ToSummary_IssueIdentifier_IsTypedAsIssueIdentifier()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Type lock test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        // .Value is a member of IssueIdentifier struct — fails to compile if the property reverts to string
        string issueIdStr = summary.IssueIdentifier; // implicit conversion fires at assignment
        issueIdStr.Should().Be("org/repo#42");
        // Also access .Value to confirm compile-time type (would not compile if property were string)
        _ = summary.IssueIdentifier.Value;
    }

    /// <summary>
    /// ToSummary must map PipelineRun.HighWaterMark to PipelineRunSummary.LastActiveStep
    /// so that BuildRunModelFromSummary can restore the correct last-reached step
    /// for terminal runs without fabricating one from the Failed/Cancelled enum ordinal.
    /// </summary>
    [Fact]
    public void ToSummary_MapsHighWaterMarkToLastActiveStep()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "LastActiveStep test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.HighWaterMark = PipelineStep.GeneratingCode;

        var summary = run.ToSummary();

        summary.LastActiveStep.Should().Be(PipelineStep.GeneratingCode);
    }

    // ── Issue #2948 — Output tail and provider ID fields ──────────────────

    [Fact]
    public void ToSummary_WithOutputLines_IncludesOutputTail()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Output tail test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        // Enqueue 500 lines — more than OutputTailCapacity (200)
        for (var i = 0; i < 500; i++)
            run.OutputLines.Enqueue($"line {i}");

        var summary = run.ToSummary();

        summary.OutputTail.Should().NotBeNull();
        summary.OutputTail!.Count.Should().Be(PipelineConstants.OutputTailCapacity);
        // Tail must contain the LAST N lines, not the first N
        summary.OutputTail.Should().Contain("line 499");
        summary.OutputTail.Should().NotContain("line 0");
        // TODO: [WARNING] The containment checks don't fully pin the boundary. An implementation returning
        // lines 1–200 (dropping only line 0) would pass because "line 0" is absent and "line 499" is not
        // checked in that path. Tighten by asserting First() == "line 300" and Last() == "line 499" to
        // fully verify that exactly the last OutputTailCapacity lines are captured in order.
    }

    [Fact]
    public void ToSummary_WithOutputLinesBelowCapacity_IncludesAllLines()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Output tail few lines test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        for (var i = 0; i < 10; i++)
            run.OutputLines.Enqueue($"line {i}");

        var summary = run.ToSummary();

        summary.OutputTail.Should().NotBeNull();
        summary.OutputTail!.Count.Should().Be(10);
        summary.OutputTail.Should().Contain("line 0");
        summary.OutputTail.Should().Contain("line 9");
    }

    [Fact]
    public void ToSummary_WithNoOutputLines_OutputTailIsNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "No output test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.OutputTail.Should().BeNull();
    }

    [Fact]
    public void ToSummary_WithProviderConfigIds_IncludesThemInSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Provider IDs test",
            IssueProviderConfigId = "ip-config-1",
            RepoProviderConfigId = "rp-config-1",
            BrainProviderConfigId = "bp-config-1",
            StartedAt = DateTime.UtcNow
        };
        run.PipelineProviderConfigId = "pp-config-1";

        var summary = run.ToSummary();

        summary.IssueProviderConfigId.Should().Be("ip-config-1");
        summary.RepoProviderConfigId.Should().Be("rp-config-1");
        summary.BrainProviderConfigId.Should().Be("bp-config-1");
        summary.PipelineProviderConfigId.Should().Be("pp-config-1");
    }

    [Fact]
    public void ToSummary_WithNoBrainOrPipelineProvider_ThoseFieldsAreNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Null provider IDs test",
            IssueProviderConfigId = "ip-config-1",
            RepoProviderConfigId = "rp-config-1",
            StartedAt = DateTime.UtcNow
            // BrainProviderConfigId defaults to null (init-only)
            // PipelineProviderConfigId defaults to null
        };

        var summary = run.ToSummary();

        summary.BrainProviderConfigId.Should().BeNull();
        summary.PipelineProviderConfigId.Should().BeNull();
        // Required provider IDs are always populated
        summary.IssueProviderConfigId.Should().Be("ip-config-1");
        summary.RepoProviderConfigId.Should().Be("rp-config-1");
    }
}
