// Feature: 020-agent-feedback-loops, Property 6: Success feedback section includes retry context and categories
// Unit tests for FeedbackPromptBuilder content verification
using System.Collections.Concurrent;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for FeedbackPromptBuilder.
/// </summary>
public class FeedbackPromptBuilderTests
{
    // Feature: 020-agent-feedback-loops, Property 6: Success feedback section includes retry context and categories
    /// <summary>
    /// Property 6: Success Feedback Section Includes Retry Context and Categories
    /// For any PipelineRun with RetryCount > 0 and non-empty RetryErrors, and any non-empty list
    /// of previous categories: the success section contains retry count, all error summaries,
    /// and all previous category labels.
    /// **Validates: Requirements 2.5, 2.6, 7.3**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(SuccessFeedbackSectionArbitraries) })]
    public void BuildSuccessFeedbackSection_IncludesRetryContextAndCategories(SuccessFeedbackSectionInput input)
    {
        var result = FeedbackPromptBuilder.BuildSuccessFeedbackSection(
            input.Run,
            input.Elapsed,
            input.PreviousHarnessCategories,
            input.PreviousIssueCategories);

        // Verify retry count is present as a string
        result.Should().Contain(input.Run.RetryCount.ToString());

        // Verify every error string from RetryErrors is present
        foreach (var error in input.Run.RetryErrors)
        {
            result.Should().Contain(error,
                because: $"the success section should include retry error: '{error}'");
        }

        // Verify every previous harness category label is present
        foreach (var category in input.PreviousHarnessCategories)
        {
            result.Should().Contain(category,
                because: $"the success section should include previous harness category: '{category}'");
        }

        // Verify every previous issue category label is present
        foreach (var category in input.PreviousIssueCategories)
        {
            result.Should().Contain(category,
                because: $"the success section should include previous issue category: '{category}'");
        }
    }
}

/// <summary>
/// Input record for the success feedback section property test (Property 6).
/// </summary>
public sealed class SuccessFeedbackSectionInput
{
    public required PipelineRun Run { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<string> PreviousHarnessCategories { get; init; }
    public required IReadOnlyList<string> PreviousIssueCategories { get; init; }

    public override string ToString() =>
        $"RetryCount={Run.RetryCount}, Errors={Run.RetryErrors.Count}, " +
        $"HarnessCategories={PreviousHarnessCategories.Count}, IssueCategories={PreviousIssueCategories.Count}";
}

/// <summary>
/// FsCheck arbitrary generators for the success feedback section property test.
/// Generates PipelineRun with RetryCount > 0 and non-empty RetryErrors,
/// plus non-empty lists of previous categories.
/// </summary>
public class SuccessFeedbackSectionArbitraries
{
    private static readonly string[] ErrorPool =
    [
        "Build failed: missing reference to System.Text.Json",
        "Test failure: Expected 42 but got 0 in CalculatorTests.Add",
        "Compilation error CS1002: ; expected in src/Main.cs line 15",
        "Test timeout: IntegrationTests.DatabaseConnection exceeded 30s",
        "Build error: Package 'Newtonsoft.Json' version conflict",
        "Test failure: NullReferenceException in UserService.GetById",
        "Lint error: unused variable 'result' in handler.ts:42",
        "Security scan: CVE-2024-1234 detected in dependency lodash@4.17.20"
    ];

    private static readonly string[] HarnessCategoryPool =
    [
        "missing file context",
        "mcp tool timeout",
        "prompt instruction gap",
        "dependency conflict",
        "test environment issue",
        "compilation error",
        "network timeout",
        "auth failure"
    ];

    private static readonly string[] IssueCategoryPool =
    [
        "contradictory acceptance criteria",
        "missing component",
        "pre-existing bug",
        "unclear requirements",
        "outdated documentation",
        "missing test fixtures"
    ];

    public static Arbitrary<SuccessFeedbackSectionInput> SuccessFeedbackSectionInputArb()
    {
        // Generate RetryCount > 0 (1 to 5)
        var retryCountGen = Gen.Choose(1, 5);

        // Generate non-empty list of retry errors (1 to 4 distinct errors)
        var retryErrorsGen =
            from count in Gen.Choose(1, 4)
            from errors in Gen.ArrayOf(Gen.Elements(ErrorPool), count)
            select errors.Distinct().ToList();

        // Generate non-empty list of previous harness categories (1 to 5)
        var harnessCategoriesGen =
            from count in Gen.Choose(1, 5)
            from categories in Gen.ArrayOf(Gen.Elements(HarnessCategoryPool), count)
            select (IReadOnlyList<string>)categories.Distinct().ToList();

        // Generate non-empty list of previous issue categories (1 to 4)
        var issueCategoriesGen =
            from count in Gen.Choose(1, 4)
            from categories in Gen.ArrayOf(Gen.Elements(IssueCategoryPool), count)
            select (IReadOnlyList<string>)categories.Distinct().ToList();

        // Generate elapsed time (1 minute to 30 minutes)
        var elapsedGen =
            from minutes in Gen.Choose(1, 30)
            from seconds in Gen.Choose(0, 59)
            select TimeSpan.FromMinutes(minutes).Add(TimeSpan.FromSeconds(seconds));

        var gen =
            from retryCount in retryCountGen
            from retryErrors in retryErrorsGen
            from harnessCategories in harnessCategoriesGen
            from issueCategories in issueCategoriesGen
            from elapsed in elapsedGen
            let run = CreatePipelineRun(retryCount, retryErrors)
            select new SuccessFeedbackSectionInput
            {
                Run = run,
                Elapsed = elapsed,
                PreviousHarnessCategories = harnessCategories,
                PreviousIssueCategories = issueCategories
            };

        return gen.ToArbitrary();
    }

    private static PipelineRun CreatePipelineRun(int retryCount, List<string> retryErrors)
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "test/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            RetryCount = retryCount
        };

        foreach (var error in retryErrors)
        {
            run.RetryErrors.Add(error);
        }

        return run;
    }
}


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
            run.RetryErrors.Add(error);
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
    /// Success section includes elapsed time in human-readable format.
    /// **Validates: Requirements 7.4**
    /// </summary>
    [Fact]
    public void BuildSuccessFeedbackSection_IncludesElapsedTime()
    {
        var run = CreateTestRun(retryCount: 1, "Compilation failed");
        var elapsed = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30);

        var result = FeedbackPromptBuilder.BuildSuccessFeedbackSection(
            run,
            elapsed,
            previousHarnessCategories: [],
            previousIssueCategories: []);

        result.Should().Contain("5m 30s");
    }

    /// <summary>
    /// Success section distinguishes harness feedback from issue feedback.
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Fact]
    public void BuildSuccessFeedbackSection_DistinguishesHarnessVsIssueFeedback()
    {
        var run = CreateTestRun(retryCount: 0);
        var elapsed = TimeSpan.FromMinutes(3);

        var result = FeedbackPromptBuilder.BuildSuccessFeedbackSection(
            run,
            elapsed,
            previousHarnessCategories: [],
            previousIssueCategories: []);

        result.Should().Contain("Harness Feedback");
        result.Should().Contain("Issue Feedback");
    }

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
    /// Success prompt instructs evidence-based answers.
    /// **Validates: Requirements 2.4, 7.5**
    /// </summary>
    [Fact]
    public void BuildSuccessFeedbackSection_InstructsEvidenceBasedAnswers()
    {
        var run = CreateTestRun(retryCount: 1, "Compilation error");
        var elapsed = TimeSpan.FromMinutes(8);

        var result = FeedbackPromptBuilder.BuildSuccessFeedbackSection(
            run,
            elapsed,
            previousHarnessCategories: [],
            previousIssueCategories: []);

        // The prompt should instruct the agent to ground answers in evidence
        result.Should().Contain("evidence");
        result.Should().Contain("file names");
        result.Should().Contain("error messages");
    }

    /// <summary>
    /// Success prompt instructs category reuse when previous categories are provided.
    /// **Validates: Requirements 2.4 (via 2.5/2.6 context)**
    /// </summary>
    [Fact]
    public void BuildSuccessFeedbackSection_InstructsCategoryReuse()
    {
        var run = CreateTestRun(retryCount: 0);
        var elapsed = TimeSpan.FromMinutes(2);
        var previousHarnessCategories = new List<string> { "missing file context", "mcp tool timeout" };
        var previousIssueCategories = new List<string> { "contradictory acceptance criteria" };

        var result = FeedbackPromptBuilder.BuildSuccessFeedbackSection(
            run,
            elapsed,
            previousHarnessCategories,
            previousIssueCategories);

        // Should instruct reuse of existing categories
        result.Should().Contain("Reuse");
        result.Should().Contain("existing");
        // Should include the actual previous categories
        result.Should().Contain("missing file context");
        result.Should().Contain("mcp tool timeout");
        result.Should().Contain("contradictory acceptance criteria");
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
}
