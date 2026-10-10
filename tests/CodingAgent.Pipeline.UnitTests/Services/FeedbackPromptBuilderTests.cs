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

    // ── Not-re-applied section: BuildStandaloneFeedbackPrompt ────────────────

    [Fact]
    public void BuildStandaloneFeedbackPrompt_WithNotReappliedIdentifiers_IncludesSection()
    {
        var run = CreateTestRun(retryCount: 0);
        run.NotReappliedIdentifiersByFile = new Dictionary<string, IReadOnlyList<string>>
        {
            ["tests/SomeTests.cs"] = ["DroppedTestClass", "DroppedMethod"]
        };
        var elapsed = TimeSpan.FromMinutes(3);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().Contain("Force-resolved rebase");
        result.Should().Contain("DroppedTestClass");
        result.Should().Contain("DroppedMethod");
        result.Should().Contain("tests/SomeTests.cs");
    }

    [Fact]
    public void BuildStandaloneFeedbackPrompt_WithEmptyNotReappliedIdentifiers_ExcludesSection()
    {
        var run = CreateTestRun(retryCount: 0);
        // NotReappliedIdentifiersByFile is empty by default
        var elapsed = TimeSpan.FromMinutes(3);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run, elapsed, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().NotContain("Force-resolved rebase");
    }

    // ── Not-re-applied section: BuildFailureFeedbackPrompt ───────────────────

    [Fact]
    public void BuildFailureFeedbackPrompt_WithNotReappliedIdentifiers_IncludesSection()
    {
        var run = CreateTestRun(retryCount: 3, "Build failed");
        run.NotReappliedIdentifiersByFile = new Dictionary<string, IReadOnlyList<string>>
        {
            ["src/ServiceA.cs"] = ["ServiceA"]
        };
        var issue = CreateTestIssue();
        var report = CreateTestReport();

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run, issue, report, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().Contain("Force-resolved rebase");
        result.Should().Contain("ServiceA");
        result.Should().Contain("src/ServiceA.cs");
    }

    [Fact]
    public void BuildFailureFeedbackPrompt_WithEmptyNotReappliedIdentifiers_ExcludesSection()
    {
        var run = CreateTestRun(retryCount: 2, "Tests failed");
        // NotReappliedIdentifiersByFile is empty by default
        var issue = CreateTestIssue();
        var report = CreateTestReport();

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run, issue, report, previousHarnessCategories: [], previousIssueCategories: []);

        result.Should().NotContain("Force-resolved rebase");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Characterization snapshot tests — added by issue #3534 as a behaviour-
    //  preserving guard before section-builder refactoring.
    //  Any prose change inside these methods will fail these tests.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildStandaloneFeedbackPrompt = """
# Pipeline Success Feedback

The pipeline completed successfully. Please provide structured feedback about this run.
Output ONLY a JSON block — no prose, no explanation, no markdown outside the JSON fence.

## Run Context

- **Elapsed time:** 7m 15s
- **Retry count:** 2

**Errors encountered during retries:**
- Compilation failed
- Tests failed

## Feedback Instructions

Based on your experience during this run, provide structured feedback.
Ground your answers in concrete evidence — reference specific file names, error messages, or tool names.

**Distinguish between:**
- **Harness feedback** — things about the pipeline, tools, or prompts that the pipeline team can fix
- **Issue feedback** — things about the issue description or repository that the issue author needs to fix

If the issue was well-written and the repo was clean, set the `issue` section to null.

### Previously Used Categories

Reuse an existing label if the root cause matches. Only create a new label if the situation is genuinely novel.

**Harness categories from recent runs:**
- prompt instruction gap

**Issue categories from recent runs:**
- missing component

## Response Format

Output ONLY the following JSON block. Reuse an existing category label if the root cause matches, or create a new short label (2-4 words) if it's genuinely novel.

```json
{
  "harness": {
    "category": "short root-cause label (2-4 words, max 50 chars)",
    "stuckReason": "what blocked progress (required for failure, max 500 chars)",
    "missingContext": ["file or data that should have been provided upfront"],
    "missingCapabilities": ["tool or ability you wished you had"],
    "promptIssues": ["confusing or contradictory instruction from the pipeline"],
    "suggestions": ["concrete improvement to the harness"]
  },
  "issue": {
    "category": "short issue-quality label (2-4 words, max 50 chars)",
    "description": "what is wrong with the issue or repository (max 500 chars)",
    "affectedFiles": ["specific file paths where problems were found"],
    "humanActionNeeded": "what the issue author should do (max 500 chars)"
  }
}
```

""";

    [Fact]
    public void BuildStandaloneFeedbackPrompt_WithCategoriesAndErrors_MatchesSnapshot()
    {
        var run = CreateTestRun(retryCount: 2, "Compilation failed", "Tests failed");
        var elapsed = TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(15);

        var result = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
            run,
            elapsed,
            previousHarnessCategories: ["prompt instruction gap"],
            previousIssueCategories: ["missing component"]);

        result.Should().Be(Snapshot_BuildStandaloneFeedbackPrompt);
    }

    private const string Snapshot_BuildFailureFeedbackPrompt = """
# Pipeline Failure Feedback

The pipeline has exhausted its retry budget and quality gates still fail.
Please provide structured feedback explaining what went wrong and what could be improved.

## Original Issue

**Title:** Fix login bug

**Description:**
The login form throws a NullReferenceException when the email field is empty.

## Retry Context

- **Retry count:** 2

**Errors encountered during retries:**
- Compilation failed
- Tests failed

## Latest Quality Gate Report

- **Compilation:** FAILED
  - Details: error CS1002: ; expected in LoginService.cs
- **Tests:** FAILED
  - Details: 3 tests failed
  - Passed: 47, Failed: 3, Skipped: 1

## Feedback Instructions

Based on the errors above and your experience during this run, provide structured feedback.
Ground your answers in concrete evidence — reference specific file names, error messages, or tool names.

**You MUST explain the `stuckReason`:** What pipeline/tool limitation or issue problem blocked progress?

**Distinguish between:**
- **Harness feedback** — things about the pipeline, tools, or prompts that the pipeline team can fix
- **Issue feedback** — things about the issue description or repository that the issue author needs to fix

If the issue itself contributed to the failure (e.g., contradictory acceptance criteria, missing component, pre-existing bug), fill the `issue` section. Otherwise, set it to null.

### Previously Used Categories

Reuse an existing label if the root cause matches. Only create a new label if the situation is genuinely novel.

**Harness categories from recent runs:**
- prompt instruction gap

**Issue categories from recent runs:**
- missing component

## Response Format

Produce a JSON block with the following structure. The `stuckReason` field is required for failure feedback. Reuse an existing category label if the root cause matches, or create a new short label (2-4 words) if it's genuinely novel.

```json
{
  "harness": {
    "category": "short root-cause label (2-4 words, max 50 chars)",
    "stuckReason": "what blocked progress (required for failure, max 500 chars)",
    "missingContext": ["file or data that should have been provided upfront"],
    "missingCapabilities": ["tool or ability you wished you had"],
    "promptIssues": ["confusing or contradictory instruction from the pipeline"],
    "suggestions": ["concrete improvement to the harness"]
  },
  "issue": {
    "category": "short issue-quality label (2-4 words, max 50 chars)",
    "description": "what is wrong with the issue or repository (max 500 chars)",
    "affectedFiles": ["specific file paths where problems were found"],
    "humanActionNeeded": "what the issue author should do (max 500 chars)"
  }
}
```

""";

    [Fact]
    public void BuildFailureFeedbackPrompt_WithCategoriesAndErrors_MatchesSnapshot()
    {
        var run = CreateTestRun(retryCount: 2, "Compilation failed", "Tests failed");
        var issue = CreateTestIssue("The login form throws a NullReferenceException when the email field is empty.");
        var report = CreateTestReport(); // defaults: compilation FAILED details, tests FAILED 47/3/1

        var result = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
            run,
            issue,
            report,
            previousHarnessCategories: ["prompt instruction gap"],
            previousIssueCategories: ["missing component"]);

        result.Should().Be(Snapshot_BuildFailureFeedbackPrompt);
    }

    // TODO [WARNING] BuildStandaloneFeedbackPrompt has no snapshot for the degenerate case where both category
    // lists are empty and there are no retry errors — the branches that suppress the "### Previously Used
    // Categories" section and the error list are not covered by any characterization guard. A regression in
    // those conditional rendering paths would not be caught. Add a snapshot test with empty category lists
    // and a run with RetryCount == 0 and no retry errors. (Review finding: FeedbackPromptBuilderTests.cs:340)

    // TODO [WARNING] BuildFailureFeedbackPrompt only has a snapshot for the case where both compilation and
    // tests fail in the quality-gate report. The path where compilation passes (or tests pass) renders
    // different output via AppendQualityGateReport; no snapshot covers those combinations. A regression in
    // the conditional rendering inside AppendQualityGateReport would not be caught. Add snapshot tests for
    // at least one additional report combination (e.g. compilation passed, tests failed).
    // (Review finding: FeedbackPromptBuilderTests.cs:396)
}
