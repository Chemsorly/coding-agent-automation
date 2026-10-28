using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Models;
using StackExchange.Redis;
using System.Reflection;

namespace CodingAgent.Orchestration.UnitTests.Redis;

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
        run.BranchName = "feature/test-branch";
        run.AgentId = "agent-xyz";
        run.ModelName = "claude-sonnet-4";
        run.FailureReason = "timeout";
        run.IssueUrl = "https://github.com/org/repo/issues/42";
        run.PullRequestUrl = "https://github.com/org/repo/pull/99";
        run.RepositoryName = "org/repo";
        run.WorkspacePath = "/tmp/ws/test-run";
        run.ProjectId = "proj-abc";
        run.ProjectName = "My Project";
        run.FinalLabel = "agent:done";

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.BranchName.Should().Be(run.BranchName);
        restored.AgentId.Should().Be(run.AgentId);
        restored.ModelName.Should().Be(run.ModelName);
        restored.FailureReason.Should().Be(run.FailureReason);
        restored.IssueUrl.Should().Be(run.IssueUrl);
        restored.PullRequestUrl.Should().Be(run.PullRequestUrl);
        restored.RepositoryName.Should().Be(run.RepositoryName);
        restored.WorkspacePath.Should().Be(run.WorkspacePath);
        restored.ProjectId.Should().Be(run.ProjectId);
        restored.ProjectName.Should().Be(run.ProjectName);
        restored.FinalLabel.Should().Be(run.FinalLabel);
    }

    /// <summary>
    /// Regression test: IssueUrl must survive the Redis hash round-trip.
    /// In distributed deployments DistributedRunService.GetRun() reconstructs runs from
    /// the hash — if IssueUrl is absent from the hash it silently returns null and the
    /// Fleet issue-link chip never renders.
    /// </summary>
    [Fact]
    public void IssueUrl_SurvivesRoundTrip()
    {
        var run = MakeMinimalRun();
        run.IssueUrl = "https://github.com/org/repo/issues/123";

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.IssueUrl.Should().Be("https://github.com/org/repo/issues/123",
            "IssueUrl must be serialised to the Redis hash so DistributedRunService.GetRun() returns it correctly");
    }

    /// <summary>
    /// When IssueUrl is null it must survive as null (not as an empty string).
    /// </summary>
    [Fact]
    public void IssueUrl_Null_SurvivesAsNull()
    {
        var run = MakeMinimalRun();
        run.IssueUrl = null;

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.IssueUrl.Should().BeNull("null IssueUrl must round-trip as null via NullIfEmpty");
    }

    // ── Integer counters ──────────────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesIntegerFields()
    {
        var run = MakeMinimalRun();
        run.FilesChangedCount = 17;
        run.LinesAdded = 342;
        run.LinesRemoved = 89;
        run.RetryCount = 2;
        run.InfrastructureRetryCount = 1;
        run.InlineCommentsPosted = 5;
        run.OpenIssuesDownloaded = 30;
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
        run.TotalTokens = 123_456_789L;
        run.CacheReadTokens = 50_000L;
        run.CacheWriteTokens = 25_000L;
        run.TotalCost = 1.2345m;
        run.BrainContextLoaded = true;
        run.BrainUpdatesPushed = true;
        run.IsDraftPr = true;
        run.AnalysisSkipped = false;
        run.MergeForceResolved = true;
        run.InlineCommentsDegraded = true;
        run.BaselineHealthPassed = true;

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

    // TODO: the three tests below (MissingIssueIdentifier, MissingIssueProviderConfigId,
    // MissingRepoProviderConfigId) all follow the same pattern and could be collapsed into a single
    // [Theory] with [InlineData] rows alongside the existing FromHash_MissingRequiredField_ReturnsNull
    // (which covers "runId"). Unifying them would make it easier to add future required fields and
    // remove the structural inconsistency where "runId" is tested separately from the other three.
    // Tracked: TestQualityReviewer warning — PipelineRunHashExtensionsPropertyTests.cs:254
    [Fact]
    public void FromHash_MissingIssueIdentifier_ReturnsNull()
    {
        var hash = MakeMinimalRun().ToHashEntries()
            .Where(e => (string)e.Name! != "issueIdentifier")
            .ToArray();

        PipelineRunHashExtensions.FromHash(hash)
            .Should().BeNull("missing required issueIdentifier must return null");
    }

    [Fact]
    public void FromHash_MissingIssueProviderConfigId_ReturnsNull()
    {
        var hash = MakeMinimalRun().ToHashEntries()
            .Where(e => (string)e.Name! != "issueProviderConfigId")
            .ToArray();

        PipelineRunHashExtensions.FromHash(hash)
            .Should().BeNull("missing required issueProviderConfigId must return null");
    }

    [Fact]
    public void FromHash_MissingRepoProviderConfigId_ReturnsNull()
    {
        var hash = MakeMinimalRun().ToHashEntries()
            .Where(e => (string)e.Name! != "repoProviderConfigId")
            .ToArray();

        PipelineRunHashExtensions.FromHash(hash)
            .Should().BeNull("missing required repoProviderConfigId must return null");
    }

    // ── Multi-field roundtrip (integration-style) ─────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_FullyPopulatedRun_PreservesAllAssertedFields()
    {
        var run = MakeMinimalRun("run-full");
        run.BranchName = "feature/full-roundtrip";
        run.AgentId = "agent-full";
        run.ModelName = "gpt-4o";
        run.IssueUrl = "https://github.com/org/repo/issues/1";
        run.PullRequestUrl = "https://github.com/org/repo/pull/1";
        run.PullRequestBody = "PR body text";
        run.PullRequestNumber = "1";
        run.FilesChangedCount = 42;
        run.LinesAdded = 1000;
        run.LinesRemoved = 200;
        run.TotalTokens = 500_000L;
        run.TotalCost = 3.14m;
        run.BrainContextLoaded = true;
        run.IsDraftPr = false;
        run.CurrentStep = PipelineStep.Completed;
        run.HighWaterMark = PipelineStep.Completed;
        run.SetCodeReviewCounts(1, 2, 3);
        run.IssueLabels = ["bug", "enhancement"];
        run.CodeReviewAgentsRun = ["agent-1", "agent-2"];

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        // Scalar fields
        restored.RunId.Should().Be("run-full");
        restored.BranchName.Should().Be("feature/full-roundtrip");
        restored.AgentId.Should().Be("agent-full");
        restored.ModelName.Should().Be("gpt-4o");
        restored.IssueUrl.Should().Be("https://github.com/org/repo/issues/1");
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

    // ── PhaseBreakdown roundtrip ───────────────────────────────────────────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesPhaseBreakdown()
    {
        var run = MakeMinimalRun();
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(
            Tokens: 12345,
            Cost: 1.23m,
            SessionCount: 3,
            AgentTimeSeconds: 42.5,
            Provider: "kiro",
            Model: "claude-sonnet-4-5")
        {
            InputTokens = 5000,
            OutputTokens = 7000,
            ReasoningTokens = 300,
            CacheReadTokens = 100,
            CacheWriteTokens = 200,
            Turns = 7,
            WebSearchRequests = 2,
            BillingMode = "subscription"
        };

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.Metrics.PhaseBreakdown.Should().HaveCount(1);
        var entry = restored.Metrics.PhaseBreakdown["codegen"];
        entry.Tokens.Should().Be(12345);
        entry.Cost.Should().Be(1.23m);
        entry.SessionCount.Should().Be(3);
        entry.AgentTimeSeconds.Should().Be(42.5);
        entry.Provider.Should().Be("kiro");
        entry.Model.Should().Be("claude-sonnet-4-5");
        entry.InputTokens.Should().Be(5000);
        entry.OutputTokens.Should().Be(7000);
        entry.ReasoningTokens.Should().Be(300);
        entry.CacheReadTokens.Should().Be(100);
        entry.CacheWriteTokens.Should().Be(200);
        entry.Turns.Should().Be(7);
        entry.WebSearchRequests.Should().Be(2);
        entry.BillingMode.Should().Be("subscription");
    }

    [Fact]
    public void FromHash_MissingPhaseBreakdown_GivesEmptyBreakdown()
    {
        var run = MakeMinimalRun();
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(Tokens: 100, Cost: 0.10m);

        // Remove the phaseBreakdown entry from the hash (simulates a hash written before this feature)
        var hash = run.ToHashEntries()
            .Where(e => (string)e.Name! != "phaseBreakdown")
            .ToArray();

        var restored = PipelineRunHashExtensions.FromHash(hash)!;

        restored.Metrics.PhaseBreakdown.Should().BeEmpty(
            "a hash without phaseBreakdown must give an empty breakdown, not throw");
    }

    [Fact]
    public void ToSummary_AfterFromHash_PreservesPhaseBreakdown()
    {
        var run = MakeMinimalRun();
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(
            Tokens: 999, Cost: 0.99m, SessionCount: 1);

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;
        var summary = restored.ToSummary();

        summary.PhaseBreakdown.Should().NotBeNull(
            "ToSummary must propagate PhaseBreakdown read back from a Redis hash");
        summary.PhaseBreakdown!.Should().ContainKey("analysis");
        summary.PhaseBreakdown!["analysis"].Tokens.Should().Be(999);
        summary.PhaseBreakdown!["analysis"].Cost.Should().Be(0.99m);
    }

    // ── Spec 053: the routed profile, model and effort ─────────────────────────

    [Fact]
    public void RouteFields_SurviveRoundTrip_AndReachTheSummary()
    {
        var run = MakeMinimalRun();
        run.AgentProfileId = "profile-kiro";
        run.AgentProfileName = "Kiro";
        run.AgentEffort = "high";
        run.ModelName = "claude-opus-4";

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.Should().BeEquivalentTo(new { AgentProfileId = "profile-kiro", AgentProfileName = "Kiro", AgentEffort = "high", ModelName = "claude-opus-4" });
        restored.ToSummary().Should().BeEquivalentTo(new { AgentProfileId = "profile-kiro", AgentProfileName = "Kiro", AgentEffort = "high" });
    }

    [Fact]
    public void RouteFields_Null_SurviveAsNull()
    {
        var restored = PipelineRunHashExtensions.FromHash(MakeMinimalRun().ToHashEntries())!;

        restored.Should().BeEquivalentTo(new { AgentProfileId = (string?)null, AgentProfileName = (string?)null, AgentEffort = (string?)null });
    }

    // ── McpServers roundtrip ───────────────────────────────────────────────

    [Fact]
    public void McpServers_SurvivesRoundTrip()
    {
        var run = MakeMinimalRun();
        run.McpServers = new List<string> { "sonarqube", "github" };

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.McpServers.Should().NotBeNull();
        restored.McpServers.Should().BeEquivalentTo(new[] { "sonarqube", "github" },
            "McpServers must survive the Redis hash round-trip");
    }

    [Fact]
    public void McpServers_Null_SurvivesAsNull()
    {
        var run = MakeMinimalRun();
        run.McpServers = null;

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.McpServers.Should().BeNull("null McpServers must round-trip as null");
    }

    [Fact]
    public void ToSummary_AfterFromHash_PreservesMcpServers()
    {
        var run = MakeMinimalRun();
        run.McpServers = new List<string> { "sonarqube", "grafana" };

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;
        var summary = restored.ToSummary();

        summary.McpServers.Should().NotBeNull();
        summary.McpServers.Should().BeEquivalentTo(new[] { "sonarqube", "grafana" },
            "McpServers must survive the Redis hash and ToSummary round-trip");
    }

    // ── PhaseBreakdown with ToolCalls roundtrip ───────────────────────────

    [Fact]
    public void PhaseBreakdown_WithToolCalls_SurvivesRoundTrip()
    {
        var run = MakeMinimalRun();
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(Tokens: 500, Cost: 0.5m)
        {
            ToolCalls = new Dictionary<string, McpCallCounts>
            {
                ["sonarqube/get_issues"] = new McpCallCounts { Calls = 7, FailedOther = 2 },
                ["web/search"] = new McpCallCounts { Calls = 3 }
            },
            UnavailableMcpServers = new Dictionary<string, string>
            {
                ["grafana"] = McpServerStatuses.NeedsAuth
            }
        };

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.Metrics.PhaseBreakdown.Should().ContainKey("codegen");
        var entry = restored.Metrics.PhaseBreakdown["codegen"];
        entry.ToolCalls.Should().NotBeNull();
        entry.ToolCalls!["sonarqube/get_issues"].Calls.Should().Be(7);
        entry.ToolCalls["sonarqube/get_issues"].FailedOther.Should().Be(2);
        entry.ToolCalls["web/search"].Calls.Should().Be(3);
        entry.UnavailableMcpServers.Should().NotBeNull();
        entry.UnavailableMcpServers!["grafana"].Should().Be(McpServerStatuses.NeedsAuth);
    }

    // ── Completion fields (HarnessVersion, RunMode, FailureCategory) ──────────

    [Fact]
    public void ToHashEntries_FromHash_PreservesCompletionFields()
    {
        var run = MakeMinimalRun();
        run.HarnessVersion = "abc1234";
        run.RunMode = RunMode.Rework;
        run.FailureCategory = FailureReason.Timeout;

        var restored = PipelineRunHashExtensions.FromHash(run.ToHashEntries())!;

        restored.HarnessVersion.Should().Be("abc1234",
            "HarnessVersion must survive the Redis hash round-trip");
        restored.RunMode.Should().Be(RunMode.Rework,
            "RunMode must survive the Redis hash round-trip");
        restored.FailureCategory.Should().Be(FailureReason.Timeout,
            "FailureCategory must survive the Redis hash round-trip");
    }

    /// <summary>
    /// Ensures that RunMode.New (enum value 0) is serialised as the string "New" and
    /// deserialised back as RunMode.New via the present-key path — not just via the
    /// absent-key ?? RunMode.New fallback. Without this test a broken JEnum that treated
    /// enum value 0 as null would pass the round-trip by accident.
    /// </summary>
    [Fact]
    public void ToHashEntries_FromHash_PreservesRunModeNew()
    {
        var run = MakeMinimalRun();
        run.RunMode = RunMode.New; // explicit zero-value round-trip

        var entries = run.ToHashEntries();

        // Assert the key is present with value "New" — distinguishes the present-key path from
        // the absent-key fallback (?? RunMode.New), which would also produce RunMode.New if the
        // key were missing, masking a broken serializer.
        var runModeEntry = entries.FirstOrDefault(e => (string)e.Name! == "runMode");
        runModeEntry.Value.ToString().Should().Be("New",
            "RunMode.New must be serialised as the string \"New\" in the hash, not omitted as an empty string");

        var restored = PipelineRunHashExtensions.FromHash(entries)!;
        restored.RunMode.Should().Be(RunMode.New,
            "RunMode.New must round-trip as the present-key value \"New\", not only via the absent-key default");
    }

    [Fact]
    public void FromHash_MissingCompletionFields_GivesDefaults()
    {
        var run = MakeMinimalRun();
        run.HarnessVersion = "abc1234";
        run.RunMode = RunMode.Rework;
        run.FailureCategory = FailureReason.Timeout;

        // Strip the three fields — simulates a hash written before this feature
        var hash = run.ToHashEntries()
            .Where(e => (string)e.Name! != "harnessVersion"
                     && (string)e.Name! != "runMode"
                     && (string)e.Name! != "failureCategory")
            .ToArray();

        var restored = PipelineRunHashExtensions.FromHash(hash)!;

        restored.HarnessVersion.Should().BeNull(
            "a hash without harnessVersion must give null, not throw");
        restored.RunMode.Should().Be(RunMode.New,
            "a hash without runMode must default to RunMode.New");
        restored.FailureCategory.Should().BeNull(
            "a hash without failureCategory must give null, not throw");
    }

    // ── Guard: every writable PipelineRun property must be stored or explicitly excluded ──

    // Not in the hash on purpose. Add a property here only with a reason.
    private static readonly Dictionary<string, string> NotInHash = new()
    {
        ["OutputLines"] = "Redis list run:{id}:output",
        ["ChatHistory"] = "Redis list run:{id}:chat",
        ["QualityGateHistory"] = "Redis list run:{id}:qg",
        ["RetryErrors"] = "Redis list run:{id}:retryerrors",
        ["CompletedAt"] = "deprecated shadow of CompletedAtOffset",
        ["PodLogTail"] = "set by RunLifecycleManager.FailRunAsync after RemoveRun",
        ["AnalysisContent"] = "set only in the agent process",
        ["CodeReviewSkipReason"] = "set only in the agent process",
        ["DroppedIdentifiersByFile"] = "set only in the agent process",
        ["NotReappliedIdentifiersByFile"] = "set only in the agent process",
        ["LinkedIssueContexts"] = "set only in the agent process",
        ["PrMarkedReadyAt"] = "set only in the agent process",
        ["BrainEvidence"] = "set only in the agent process",
        ["BrainSkills"] = "set only in the agent process",
        ["StackLabels"] = "set only in the agent process",
    };

    [Fact]
    public void ToHashEntries_StoresEveryWritableProperty()
    {
        // Collect all public instance properties with a public set or init accessor.
        var writableProps = typeof(PipelineRun)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        // Derive the expected hash field name for each property.
        // Rule: lower-case first letter, plus two explicit renames.
        static string ToHashFieldName(string propName) => propName switch
        {
            "DecompositionSubIssuesCreated" => "decompSubIssuesCreated",
            "DecompositionSubIssuesAttempted" => "decompSubIssuesAttempted",
            _ => char.ToLowerInvariant(propName[0]) + propName[1..]
        };

        // Get all field names actually present in a minimal run's hash.
        var hashFieldNames = MakeMinimalRun()
            .ToHashEntries()
            .Select(e => (string)e.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var prop in writableProps)
        {
            if (NotInHash.ContainsKey(prop)) continue;
            var fieldName = ToHashFieldName(prop);
            if (!hashFieldNames.Contains(fieldName))
                missing.Add($"{prop} (expected hash key: \"{fieldName}\")");
        }

        missing.Should().BeEmpty(
            "every writable PipelineRun property must be stored in the hash or listed in NotInHash with a reason");
    }
}
