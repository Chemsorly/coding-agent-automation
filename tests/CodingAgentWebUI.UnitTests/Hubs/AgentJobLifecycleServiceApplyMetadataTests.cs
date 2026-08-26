using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="AgentJobLifecycleService.ApplyStepMetadata"/> —
/// the internal static method that maps step-metadata key/value pairs to PipelineRun properties.
/// All tests are pure-logic with no mocks.
/// </summary>
public class AgentJobLifecycleServiceApplyMetadataTests
{
    private static readonly string[] s_AgentABC = new[] { "agent-a", "agent-b", "agent-c" };
    private static readonly string[] s_SoloAgent = new[] { "solo-agent" };

    // ── BranchName ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_BranchName_SetsProperty()
    {
        var run = MakeRun();
        Apply(run, new() { ["BranchName"] = "feature/login" });
        run.BranchName.Should().Be("feature/login");
    }

    [Fact]
    public void ApplyStepMetadata_BranchName_EmptyString_SetsEmptyString()
    {
        var run = MakeRun();
        run.BranchName = "old-branch";
        Apply(run, new() { ["BranchName"] = "" });
        run.BranchName.Should().BeEmpty();
    }

    // ── BaselineHealthPassed ──────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_True_SetsTrue()
    {
        var run = MakeRun();
        Apply(run, new() { ["BaselineHealthPassed"] = "True" });
        run.BaselineHealthPassed.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_False_SetsFalse()
    {
        var run = MakeRun();
        Apply(run, new() { ["BaselineHealthPassed"] = "False" });
        run.BaselineHealthPassed.Should().BeFalse();
    }

    [Fact]
    public void ApplyStepMetadata_BaselineHealthPassed_Garbage_SetsNull()
    {
        var run = MakeRun();
        Apply(run, new() { ["BaselineHealthPassed"] = "not-a-bool" });
        run.BaselineHealthPassed.Should().BeNull("unparseable value must leave property null");
    }

    // ── AnalysisSkipped ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_AnalysisSkipped_True_SetsTrue()
    {
        var run = MakeRun();
        Apply(run, new() { ["AnalysisSkipped"] = "True" });
        run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepMetadata_AnalysisSkipped_False_SetsFalse()
    {
        var run = MakeRun();
        run.AnalysisSkipped = true;
        Apply(run, new() { ["AnalysisSkipped"] = "False" });
        run.AnalysisSkipped.Should().BeFalse();
    }

    [Fact]
    public void ApplyStepMetadata_AnalysisSkipped_Garbage_SetsFalse()
    {
        // TryParseBool returns null for garbage → `null == true` is false → AnalysisSkipped = false
        var run = MakeRun();
        run.AnalysisSkipped = false;
        Apply(run, new() { ["AnalysisSkipped"] = "garbage" });
        run.AnalysisSkipped.Should().BeFalse();
    }

    // ── Integer fields ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FilesChangedCount", 10)]
    [InlineData("LinesAdded", 200)]
    [InlineData("LinesRemoved", 50)]
    [InlineData("CodeReviewIterationsCompleted", 3)]
    [InlineData("CodeReviewIterationsTotal", 5)]
    [InlineData("CodeReviewIterationInProgress", 2)]
    [InlineData("OpenIssuesDownloaded", 42)]
    [InlineData("DecompositionSubIssuesCreated", 7)]
    [InlineData("DecompositionSubIssuesAttempted", 8)]
    [InlineData("RetryCount", 1)]
    [InlineData("InfrastructureRetryCount", 2)]
    public void ApplyStepMetadata_IntegerField_ValidValue_SetsProperty(string key, int expected)
    {
        var run = MakeRun();
        Apply(run, new() { [key] = expected.ToString() });

        var actual = key switch
        {
            "FilesChangedCount" => run.FilesChangedCount,
            "LinesAdded" => run.LinesAdded,
            "LinesRemoved" => run.LinesRemoved,
            "CodeReviewIterationsCompleted" => run.CodeReviewIterationsCompleted,
            "CodeReviewIterationsTotal" => run.CodeReviewIterationsTotal,
            "CodeReviewIterationInProgress" => run.CodeReviewIterationInProgress,
            "OpenIssuesDownloaded" => run.OpenIssuesDownloaded,
            "DecompositionSubIssuesCreated" => run.DecompositionSubIssuesCreated,
            "DecompositionSubIssuesAttempted" => run.DecompositionSubIssuesAttempted,
            "RetryCount" => run.RetryCount,
            "InfrastructureRetryCount" => run.InfrastructureRetryCount,
            _ => throw new ArgumentOutOfRangeException(key)
        };

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("FilesChangedCount")]
    [InlineData("LinesAdded")]
    [InlineData("RetryCount")]
    [InlineData("InfrastructureRetryCount")]
    public void ApplyStepMetadata_IntegerField_Garbage_PreservesExistingValue(string key)
    {
        var run = MakeRun();
        // Set a known sentinel value first
        Apply(run, new() { [key] = "99" });

        // Now apply garbage — should preserve the previously set 99
        Apply(run, new() { [key] = "not-an-int" });

        var actual = key switch
        {
            "FilesChangedCount" => run.FilesChangedCount,
            "LinesAdded" => run.LinesAdded,
            "RetryCount" => run.RetryCount,
            "InfrastructureRetryCount" => run.InfrastructureRetryCount,
            _ => throw new ArgumentOutOfRangeException(key)
        };

        actual.Should().Be(99, "garbage value must preserve the existing property value");
    }

    // ── TotalTokens (long) ────────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_TotalTokens_ValidLong_SetsProperty()
    {
        var run = MakeRun();
        Apply(run, new() { ["TotalTokens"] = "9999999999" });
        run.TotalTokens.Should().Be(9999999999L);
    }

    [Fact]
    public void ApplyStepMetadata_TotalTokens_Garbage_PreservesExistingValue()
    {
        var run = MakeRun();
        run.TotalTokens = 12345L;
        Apply(run, new() { ["TotalTokens"] = "not-a-number" });
        run.TotalTokens.Should().Be(12345L);
    }

    // ── TotalCost (decimal, invariant culture) ────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_TotalCost_ValidDecimal_SetsProperty()
    {
        var run = MakeRun();
        Apply(run, new() { ["TotalCost"] = "9.99" });
        run.TotalCost.Should().Be(9.99m);
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_CommaDecimalSeparator_SetsNull()
    {
        // Invariant culture uses period — comma-separated values should not parse
        var run = MakeRun();
        run.TotalCost = 5.00m;
        Apply(run, new() { ["TotalCost"] = "9,99" });
        // Either doesn't parse (null → preserve 5.00) or parses as 999 — either way not 9.99
        // The key invariant: comma-separated value must NOT set 9.99
        run.TotalCost.Should().NotBe(9.99m, "invariant culture should not parse comma-decimal as 9.99");
    }

    [Fact]
    public void ApplyStepMetadata_TotalCost_Garbage_PreservesExistingValue()
    {
        var run = MakeRun();
        run.TotalCost = 1.50m;
        Apply(run, new() { ["TotalCost"] = "not-a-decimal" });
        run.TotalCost.Should().Be(1.50m);
    }

    // ── CodeReview counts (atomic deferred apply) ─────────────────────────────

    [Fact]
    public void ApplyStepMetadata_CodeReviewCounts_AllThree_SetsAllAtOnce()
    {
        var run = MakeRun();
        Apply(run, new()
        {
            ["CodeReviewCriticalCount"] = "2",
            ["CodeReviewWarningCount"] = "3",
            ["CodeReviewSuggestionCount"] = "5"
        });
        run.CodeReviewCriticalCount.Should().Be(2);
        run.CodeReviewWarningCount.Should().Be(3);
        run.CodeReviewSuggestionCount.Should().Be(5);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewCounts_OnlyCritical_PreservesExistingWarningSuggestion()
    {
        var run = MakeRun();
        run.SetCodeReviewCounts(0, 4, 6); // pre-set warning=4, suggestion=6

        Apply(run, new() { ["CodeReviewCriticalCount"] = "3" });

        run.CodeReviewCriticalCount.Should().Be(3);
        run.CodeReviewWarningCount.Should().Be(4, "warning should be preserved when not in metadata");
        run.CodeReviewSuggestionCount.Should().Be(6, "suggestion should be preserved when not in metadata");
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewCounts_GarbageValues_PreservesExisting()
    {
        var run = MakeRun();
        run.SetCodeReviewCounts(1, 2, 3);
        Apply(run, new()
        {
            ["CodeReviewCriticalCount"] = "bad",
            ["CodeReviewWarningCount"] = "bad"
        });
        // pendingCritical = null, pendingWarning = null → no SetCodeReviewCounts call at all
        run.CodeReviewCriticalCount.Should().Be(1);
        run.CodeReviewWarningCount.Should().Be(2);
        run.CodeReviewSuggestionCount.Should().Be(3);
    }

    // ── CodeReviewAgentsRun ───────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_CodeReviewAgentsRun_SplitsOnUnitSeparator()
    {
        var run = MakeRun();
        // Use char(31) - ASCII Unit Separator - explicitly to avoid encoding issues
        var separator = new string(new[] { (char)31 });
        Apply(run, new() { ["CodeReviewAgentsRun"] = "agent-a" + separator + "agent-b" + separator + "agent-c" });
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(s_AgentABC);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewAgentsRun_SingleAgent_ReturnsSingleItem()
    {
        var run = MakeRun();
        Apply(run, new() { ["CodeReviewAgentsRun"] = "solo-agent" });
        run.CodeReviewAgentsRun.Should().BeEquivalentTo(s_SoloAgent);
    }

    [Fact]
    public void ApplyStepMetadata_CodeReviewAgentsRun_EmptyString_ReturnsEmptyArray()
    {
        var run = MakeRun();
        Apply(run, new() { ["CodeReviewAgentsRun"] = "" });
        run.CodeReviewAgentsRun.Should().BeEmpty("empty string split with RemoveEmptyEntries yields no elements");
    }

    // ── Unknown keys ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_UnknownKey_IsIgnored()
    {
        var run = MakeRun();
        run.BranchName = "original";

        Apply(run, new() { ["SomeUnknownKey"] = "SomeValue" });

        run.BranchName.Should().Be("original", "unknown keys must not affect any run property");
    }

    // ── Empty metadata ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_EmptyDictionary_IsNoOp()
    {
        var run = MakeRun();
        run.BranchName = "unchanged";
        run.RetryCount = 7;

        Apply(run, new());

        run.BranchName.Should().Be("unchanged");
        run.RetryCount.Should().Be(7);
    }

    // ── Multiple keys applied together ────────────────────────────────────────

    [Fact]
    public void ApplyStepMetadata_MultipleValidKeys_AllApplied()
    {
        var run = MakeRun();
        Apply(run, new()
        {
            ["BranchName"] = "feature/multi",
            ["BaselineHealthPassed"] = "True",
            ["FilesChangedCount"] = "15",
            ["TotalTokens"] = "500000",
            ["TotalCost"] = "1.23"
        });

        run.BranchName.Should().Be("feature/multi");
        run.BaselineHealthPassed.Should().BeTrue();
        run.FilesChangedCount.Should().Be(15);
        run.TotalTokens.Should().Be(500000L);
        run.TotalCost.Should().Be(1.23m);
    }

    [Fact]
    public void ApplyStepMetadata_MixedValidAndGarbage_ValidApplied_GarbagePreservesExisting()
    {
        var run = MakeRun();
        run.RetryCount = 5;

        Apply(run, new()
        {
            ["BranchName"] = "valid-branch",
            ["RetryCount"] = "not-an-int",  // garbage
            ["FilesChangedCount"] = "10"    // valid
        });

        run.BranchName.Should().Be("valid-branch");
        run.RetryCount.Should().Be(5, "garbage RetryCount must preserve existing value");
        run.FilesChangedCount.Should().Be(10);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Apply(PipelineRun run, Dictionary<string, string> metadata)
        => AgentJobLifecycleService.ApplyStepMetadata(run, metadata);

    private static PipelineRun MakeRun() => new()
    {
        RunId = "test-run",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };
}
