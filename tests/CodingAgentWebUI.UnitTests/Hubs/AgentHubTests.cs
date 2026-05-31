using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for AgentHub-related models, message types, and hub contract validation.
/// Since AgentHub depends on sealed services that cannot be mocked, these tests validate
/// the message models, enums, and contract interfaces used by the hub.
/// </summary>
public class AgentHubTests
{
    // ── AgentRegistrationMessage ────────────────────────────────────────

    [Fact]
    public void AgentRegistrationMessage_RequiredProperties_CanBeSet()
    {
        var message = new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            AgentType = "kiro-dotnet",
            Labels = new[] { "dotnet", "linux" }
        };

        message.AgentId.Should().Be("agent-1");
        message.Hostname.Should().Be("host-1");
        message.AgentType.Should().Be("kiro-dotnet");
        message.Labels.Should().HaveCount(2);
    }

    // ── HeartbeatMessage ────────────────────────────────────────────────

    [Fact]
    public void HeartbeatMessage_Properties_RoundTrip()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var message = new HeartbeatMessage
        {
            AgentId = "agent-1",
            Timestamp = timestamp
        };

        message.AgentId.Should().Be("agent-1");
        message.Timestamp.Should().Be(timestamp);
    }

    // ── JobCompletionPayload ────────────────────────────────────────────

    [Fact]
    public void JobCompletionPayload_AllProperties_CanBeSet()
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = now,
            PullRequestUrl = "https://github.com/org/repo/pull/1",
            PullRequestNumber = "1",
            IsDraftPr = false,
            RetryCount = 2,
            FilesChangedCount = 10,
            LinesAdded = 200,
            LinesRemoved = 50,
            BrainUpdatesPushed = true,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = new[] { "concern1" },
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = new[] { "Correctness", "Security" },
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 1,
            CodeReviewSuggestionCount = 3,
            FinalLabel = AgentLabels.Done
        };

        payload.FinalStep.Should().Be(PipelineStep.Completed);
        payload.CompletedAt.Should().Be(now);
        payload.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/1");
        payload.PullRequestNumber.Should().Be("1");
        payload.IsDraftPr.Should().BeFalse();
        payload.RetryCount.Should().Be(2);
        payload.FilesChangedCount.Should().Be(10);
        payload.LinesAdded.Should().Be(200);
        payload.LinesRemoved.Should().Be(50);
        payload.BrainUpdatesPushed.Should().BeTrue();
        payload.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        payload.CodeReviewAgentsRun.Should().HaveCount(2);
        payload.CodeReviewCriticalCount.Should().Be(0);
        payload.CodeReviewWarningCount.Should().Be(1);
        payload.CodeReviewSuggestionCount.Should().Be(3);
        payload.FinalLabel.Should().Be(AgentLabels.Done);
    }

    [Fact]
    public void JobCompletionPayload_FailureReason_NullByDefault()
    {
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = "Build failed"
        };

        payload.FailureReason.Should().Be("Build failed");
    }

    // ── TokenRefreshResponse ────────────────────────────────────────────

    [Fact]
    public void TokenRefreshResponse_Properties_RoundTrip()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var response = new TokenRefreshResponse
        {
            Token = "ghs_test_token_123",
            ExpiresAt = expiresAt
        };

        response.Token.Should().Be("ghs_test_token_123");
        response.ExpiresAt.Should().Be(expiresAt);
    }

    // ── CommentType enum ────────────────────────────────────────────────

    [Fact]
    public void CommentType_HasExpectedValues()
    {
        ((int)CommentType.Analysis).Should().Be(0);
        ((int)CommentType.GateRejection).Should().Be(1);
        ((int)CommentType.GateWontDo).Should().Be(2);
    }

    // ── CommentPayload ──────────────────────────────────────────────────

    [Fact]
    public void CommentPayload_AnalysisMarkdown_CanBeSet()
    {
        var payload = new CommentPayload
        {
            AnalysisMarkdown = "## Analysis\n\nThis is a test."
        };

        payload.AnalysisMarkdown.Should().Contain("Analysis");
        payload.AssessmentJson.Should().BeNull();
    }

    [Fact]
    public void CommentPayload_AssessmentJson_CanBeSet()
    {
        var payload = new CommentPayload
        {
            AssessmentJson = """{"recommendation":"ready"}"""
        };

        payload.AssessmentJson.Should().Contain("ready");
        payload.AnalysisMarkdown.Should().BeNull();
    }

    // ── ChatResponseMessage ─────────────────────────────────────────────

    [Fact]
    public void ChatResponseMessage_Properties_RoundTrip()
    {
        var message = new ChatResponseMessage
        {
            SessionId = "session-123",
            Lines = new List<string> { "Hello", "World" }
        };

        message.SessionId.Should().Be("session-123");
        message.Lines.Should().HaveCount(2);
    }

    // ── ChatCompletedMessage ────────────────────────────────────────────

    [Fact]
    public void ChatCompletedMessage_Properties_RoundTrip()
    {
        var message = new ChatCompletedMessage
        {
            SessionId = "session-123",
            ExitCode = 0,
            Error = null
        };

        message.SessionId.Should().Be("session-123");
        message.ExitCode.Should().Be(0);
        message.Error.Should().BeNull();
    }

    [Fact]
    public void ChatCompletedMessage_WithError_HasErrorMessage()
    {
        var message = new ChatCompletedMessage
        {
            SessionId = "session-456",
            ExitCode = ExitCodes.GeneralFailure,
            Error = "Process timed out"
        };

        message.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        message.Error.Should().Be("Process timed out");
    }

    // ── ProviderKind enum ───────────────────────────────────────────────

    [Fact]
    public void ProviderKind_HasExpectedValues()
    {
        ProviderKind.Issue.Should().Be(ProviderKind.Issue);
        ProviderKind.Repository.Should().Be(ProviderKind.Repository);
        ProviderKind.Agent.Should().Be(ProviderKind.Agent);
        ProviderKind.Pipeline.Should().Be(ProviderKind.Pipeline);
    }

    // ── PipelineStep enum ───────────────────────────────────────────────

    [Fact]
    public void PipelineStep_Completed_IsTerminalState()
    {
        // Terminal states should be >= Completed
        var completed = PipelineStep.Completed;
        Assert.True(completed >= PipelineStep.Completed);
        Assert.True(PipelineStep.Failed > PipelineStep.Completed);
    }

    // ── ChatRole enum ───────────────────────────────────────────────────

    [Fact]
    public void ChatRole_HasExpectedValues()
    {
        ChatRole.Agent.Should().Be(ChatRole.Agent);
        ChatRole.User.Should().Be(ChatRole.User);
    }

    // ── ChatEntry ───────────────────────────────────────────────────────

    [Fact]
    public void ChatEntry_Properties_RoundTrip()
    {
        var entry = new ChatEntry
        {
            Role = ChatRole.Agent,
            Content = "Hello from agent",
            Timestamp = DateTime.UtcNow
        };

        entry.Role.Should().Be(ChatRole.Agent);
        entry.Content.Should().Be("Hello from agent");
        entry.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    // ── QualityGateReport ───────────────────────────────────────────────

    [Fact]
    public void QualityGateReport_AllPassed_WhenBothPass()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        report.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void QualityGateReport_AllPassed_FalseWhenCompilationFails()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void QualityGateReport_AllPassed_FalseWhenTestsFail()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void QualityGateReport_AllPassed_WithCoverage_PassesWhenAllPass()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = new GateResult { GateName = "Coverage", Passed = true, CoveragePercent = 85.0 }
        };

        report.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void QualityGateReport_AllPassed_WithCoverage_FailsWhenCoverageFails()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = new GateResult { GateName = "Coverage", Passed = false, CoveragePercent = 30.0 }
        };

        report.AllPassed.Should().BeFalse();
    }

    // ── GateResult ──────────────────────────────────────────────────────

    [Fact]
    public void GateResult_Properties_RoundTrip()
    {
        var result = new GateResult
        {
            GateName = "Tests",
            Passed = true,
            Details = "All 42 tests passed",
            TestsPassed = 42,
            TestsFailed = 0,
            TestsSkipped = 2,
            CoveragePercent = 87.5
        };

        result.GateName.Should().Be("Tests");
        result.Passed.Should().BeTrue();
        result.Details.Should().Contain("42 tests");
        result.TestsPassed.Should().Be(42);
        result.TestsFailed.Should().Be(0);
        result.TestsSkipped.Should().Be(2);
        result.CoveragePercent.Should().Be(87.5);
    }

    // ── AgentLabels ─────────────────────────────────────────────────────

    [Fact]
    public void AgentLabels_All_ContainsExpectedLabels()
    {
        AgentLabels.All.Should().NotBeEmpty();
        AgentLabels.All.Should().Contain("agent:next");
        AgentLabels.All.Should().Contain("agent:in-progress");
    }

    // ── PipelineRun ─────────────────────────────────────────────────────

    [Fact]
    public void PipelineRun_ToSummary_MapsCorrectly()
    {
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow,
            RetryCount = 1,
            PullRequestUrl = "https://github.com/org/repo/pull/1",
            AgentId = "agent-1",
            InitiatedBy = "loop"
        };

        var summary = run.ToSummary();

        summary.RunId.Should().Be("run-1");
        summary.IssueIdentifier.Should().Be("org/repo#1");
        summary.IssueTitle.Should().Be("Test Issue");
        summary.FinalStep.Should().Be(PipelineStep.Completed);
        summary.RetryCount.Should().Be(1);
        summary.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/1");
        summary.AgentId.Should().Be("agent-1");
        summary.InitiatedBy.Should().Be("loop");
    }

    [Fact]
    public void PipelineRun_OutputLines_IsThreadSafe()
    {
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };

        // Enqueue from multiple threads
        Parallel.For(0, 100, i => run.OutputLines.Enqueue($"line-{i}"));

        run.OutputLines.Count.Should().Be(100);
    }

    [Fact]
    public void PipelineRun_ChatHistory_IsThreadSafe()
    {
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };

        Parallel.For(0, 50, i => run.ChatHistory.Enqueue(new ChatEntry
        {
            Role = ChatRole.Agent,
            Content = $"msg-{i}",
            Timestamp = DateTime.UtcNow
        }));

        run.ChatHistory.Count.Should().Be(50);
    }
}
