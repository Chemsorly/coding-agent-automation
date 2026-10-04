// Unit tests for FeedbackPromptBuilder content verification
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests verifying FeedbackPromptBuilder produces prompts with correct content.
/// **Validates: Requirements 2.3, 2.4, 3.3, 3.4, 7.4, 7.5**
/// </summary>
public class FeedbackPromptBuilderContentTests
{
    private static PipelineRun CreateTestRun(int retryCount = 2, params string[] retryErrors)
    {
        var run = new PipelineRun
        {
            RunId = "test-run-001",
            IssueIdentifier = "42",
            IssueTitle = "Fix login bug",
            IssueProviderConfigId = "config-1",
            RepoProviderConfigId = "config-2",
            StartedAt = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
            RetryCount = retryCount
        };

        foreach (var error in retryErrors)
        {
            run.RetryErrors.Enqueue(error);
        }

        return run;
    }

    private static IssueDetail CreateTestIssue(string? description = null) => new()
    {
        Identifier = "42",
        Title = "Fix login bug",
        Description = description ?? "The login form throws a NullReferenceException when the email field is empty.",
        Labels = ["bug", "priority:high"]
    };

    private static QualityGateReport CreateTestReport(
        bool compilationPassed = false,
        bool testsPassed = false,
        string? compilationDetails = null,
        string? testDetails = null) => new()
    {
        Compilation = new GateResult
        {
            GateName = "Compilation",
            Passed = compilationPassed,
            Details = compilationDetails ?? "error CS1002: ; expected in LoginService.cs"
        },
        Tests = new GateResult
        {
            GateName = "Tests",
            Passed = testsPassed,
            Details = testDetails ?? "3 tests failed",
            TestsPassed = 47,
            TestsFailed = 3,
            TestsSkipped = 1
        }
    };

    /// <summary>
    /// Failure prompt includes the issue description text.
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Fact]
    public void BuildFailureFeedbackPrompt_IncludesIssueDescription()
    {
        var run = CreateTestRun(retryCount: 3, "Compilation failed", "Tests failed");
        var issue = CreateTestIssue("The login form throws a NullReferenceException when the email field is empty.");
        var report = CreateTestReport();

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run,
            issue,
            report,
            previousHarnessCategories: [],
            previousIssueCategories: []);

        result.Should().Contain("The login form throws a NullReferenceException when the email field is empty.");
    }

    /// <summary>
    /// Failure prompt instructs evidence-based answers (references file names, error messages, or tool names).
    /// **Validates: Requirements 3.4, 7.5**
    /// </summary>
    [Fact]
    public void BuildFailureFeedbackPrompt_InstructsEvidenceBasedAnswers()
    {
        var run = CreateTestRun(retryCount: 2, "Test failure");
        var issue = CreateTestIssue();
        var report = CreateTestReport();

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run,
            issue,
            report,
            previousHarnessCategories: [],
            previousIssueCategories: []);

        // The prompt should instruct the agent to ground answers in evidence
        result.Should().Contain("evidence");
        result.Should().Contain("file names");
        result.Should().Contain("error messages");
    }

    /// <summary>
    /// Failure prompt instructs category reuse when previous categories are provided.
    /// **Validates: Requirements 3.4 (via 3.5/3.6 context)**
    /// </summary>
    [Fact]
    public void BuildFailureFeedbackPrompt_InstructsCategoryReuse()
    {
        var run = CreateTestRun(retryCount: 3, "Build failed");
        var issue = CreateTestIssue();
        var report = CreateTestReport();
        var previousHarnessCategories = new List<string> { "prompt instruction gap" };
        var previousIssueCategories = new List<string> { "missing component" };

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run,
            issue,
            report,
            previousHarnessCategories,
            previousIssueCategories);

        // Should instruct reuse of existing categories
        result.Should().Contain("Reuse");
        result.Should().Contain("existing");
        // Should include the actual previous categories
        result.Should().Contain("prompt instruction gap");
        result.Should().Contain("missing component");
    }

    // ── BuildStandaloneFeedbackPrompt tests ──

    [Fact]
    public void BuildStandaloneFeedbackPrompt_IsStandalonePrompt()
    {
        var run = CreateTestRun(retryCount: 1, "Compilation failed");
        var elapsed = TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(15);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().Contain("Pipeline Success Feedback");
        result.Should().Contain("Output ONLY a JSON block");
        result.Should().Contain("7m 15s");
    }

    [Fact]
    public void BuildStandaloneFeedbackPrompt_IncludesRetryContext()
    {
        var run = CreateTestRun(retryCount: 2, "Build failed", "Tests failed");
        var elapsed = TimeSpan.FromMinutes(4);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().Contain("2");
        result.Should().Contain("Build failed");
        result.Should().Contain("Tests failed");
    }

    [Fact]
    public void BuildStandaloneFeedbackPrompt_IncludesPreviousCategories()
    {
        var run = CreateTestRun(retryCount: 0);
        var elapsed = TimeSpan.FromMinutes(3);
        var harnessCategories = new List<string> { "missing file context" };
        var issueCategories = new List<string> { "contradictory acceptance criteria" };

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, harnessCategories, issueCategories);

        result.Should().Contain("missing file context");
        result.Should().Contain("contradictory acceptance criteria");
    }

    [Fact]
    public void BuildStandaloneFeedbackPrompt_IncludesJsonSchema()
    {
        var run = CreateTestRun(retryCount: 0);
        var elapsed = TimeSpan.FromMinutes(2);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().Contain("\"harness\"");
        result.Should().Contain("\"category\"");
        result.Should().Contain("\"issue\"");
    }

    [Fact]
    public void BuildStandaloneFeedbackPrompt_NullRun_Throws()
    {
        var act = () => FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            null!, TimeSpan.FromMinutes(1), [], []);
        act.Should().Throw<ArgumentNullException>();
    }
}
