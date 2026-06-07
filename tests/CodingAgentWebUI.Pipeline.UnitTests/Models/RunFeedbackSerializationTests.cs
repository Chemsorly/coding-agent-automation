// Feature: 020-agent-feedback-loops, Property 1: RunFeedback serialization round-trip
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Property-based tests for RunFeedback JSON serialization round-trips.
/// **Validates: Requirements 1.1, 1.2, 1.3, 1.5**
/// </summary>
public class RunFeedbackSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Property 1: RunFeedback Serialization Round-Trip
    /// For any valid RunFeedback instance, serializing to JSON via System.Text.Json
    /// and deserializing back SHALL produce a structurally equivalent object with all fields preserved.
    /// **Validates: Requirements 1.1, 1.2, 1.3, 1.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(RunFeedbackSerializationArbitraries) })]
    public void RunFeedback_JsonRoundTrip_PreservesAllFields(RunFeedback original)
    {
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RunFeedback>(json, JsonOptions);

        deserialized.Should().NotBeNull();

        // Top-level fields
        deserialized!.Outcome.Should().Be(original.Outcome);
        deserialized.CollectedAtUtc.Should().Be(original.CollectedAtUtc);

        // HarnessFeedback
        deserialized.Harness.Should().NotBeNull();
        deserialized.Harness.Category.Should().Be(original.Harness.Category);
        deserialized.Harness.StuckReason.Should().Be(original.Harness.StuckReason);
        deserialized.Harness.MissingContext.Should().BeEquivalentTo(original.Harness.MissingContext);
        deserialized.Harness.MissingCapabilities.Should().BeEquivalentTo(original.Harness.MissingCapabilities);
        deserialized.Harness.PromptIssues.Should().BeEquivalentTo(original.Harness.PromptIssues);
        deserialized.Harness.Suggestions.Should().BeEquivalentTo(original.Harness.Suggestions);

        // IssueFeedback (nullable)
        if (original.Issue is null)
        {
            deserialized.Issue.Should().BeNull();
        }
        else
        {
            deserialized.Issue.Should().NotBeNull();
            deserialized.Issue!.Category.Should().Be(original.Issue.Category);
            deserialized.Issue.Description.Should().Be(original.Issue.Description);
            deserialized.Issue.AffectedFiles.Should().BeEquivalentTo(original.Issue.AffectedFiles);
            deserialized.Issue.HumanActionNeeded.Should().Be(original.Issue.HumanActionNeeded);
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for RunFeedback serialization property tests.
/// Generates valid RunFeedback instances with:
/// - Both FeedbackOutcome values (Success, Failure)
/// - Nullable and non-null IssueFeedback
/// - Various string lengths including null values
/// - Lists of varying sizes (0 to max items via SubListOf)
/// </summary>
public class RunFeedbackSerializationArbitraries
{
    private static readonly string[] CategoryPool =
    [
        "missing file context",
        "mcp tool timeout",
        "prompt instruction gap",
        "contradictory acceptance criteria",
        "missing component",
        "pre-existing bug"
    ];

    private static readonly string[] StuckReasonPool =
    [
        "Could not find the referenced file src/main.ts",
        "MCP server timed out after 30 seconds",
        "The prompt said to use React but the project uses Vue",
        "Build failed due to missing dependency",
        "Test framework not configured correctly"
    ];

    private static readonly string[] ContextPool =
    [
        "tsconfig.json was needed for path resolution",
        "The .env file was not provided",
        "Database schema was missing",
        "API documentation not available",
        "Previous PR diff would have helped"
    ];

    private static readonly string[] CapabilityPool =
    [
        "Run integration tests",
        "Access to database",
        "Browser preview",
        "Git blame history",
        "Deploy to staging"
    ];

    private static readonly string[] PromptIssuePool =
    [
        "Prompt says 'use latest version' but doesn't specify which",
        "Contradictory instructions about error handling",
        "Missing context about project architecture",
        "Unclear acceptance criteria for edge cases",
        "Instructions reference non-existent tool"
    ];

    private static readonly string[] SuggestionPool =
    [
        "Include tsconfig.json in initial context",
        "Add timeout retry for MCP calls",
        "Clarify which framework version to target"
    ];

    private static readonly string[] FilePool =
    [
        "src/main.ts",
        "src/components/App.vue",
        "tests/unit/helper.test.js",
        "package.json",
        "README.md"
    ];

    private static readonly string[] DescriptionPool =
    [
        "Issue acceptance criteria are contradictory",
        "Referenced component does not exist in the repo",
        "Pre-existing bug in the area that needs modification",
        "Missing setup instructions for local development"
    ];

    private static readonly string[] HumanActionPool =
    [
        "Fix the contradictory acceptance criteria",
        "Add the missing component referenced in the issue",
        "Update the README with correct setup instructions"
    ];

    public static Arbitrary<RunFeedback> RunFeedbackArb()
    {
        var harnessFeedbackGen =
            from hasCategory in Gen.Elements(true, false)
            from category in Gen.Elements(CategoryPool)
            from hasStuckReason in Gen.Elements(true, false)
            from stuckReason in Gen.Elements(StuckReasonPool)
            from missingContext in Gen.SubListOf(ContextPool)
            from missingCapabilities in Gen.SubListOf(CapabilityPool)
            from promptIssues in Gen.SubListOf(PromptIssuePool)
            from suggestions in Gen.SubListOf(SuggestionPool)
            select new HarnessFeedback
            {
                Category = hasCategory ? category : null,
                StuckReason = hasStuckReason ? stuckReason : null,
                MissingContext = missingContext.ToList(),
                MissingCapabilities = missingCapabilities.ToList(),
                PromptIssues = promptIssues.ToList(),
                Suggestions = suggestions.ToList()
            };

        var issueFeedbackGen =
            from hasCategory in Gen.Elements(true, false)
            from category in Gen.Elements(CategoryPool)
            from hasDescription in Gen.Elements(true, false)
            from description in Gen.Elements(DescriptionPool)
            from affectedFiles in Gen.SubListOf(FilePool)
            from hasHumanAction in Gen.Elements(true, false)
            from humanAction in Gen.Elements(HumanActionPool)
            select new IssueFeedback
            {
                Category = hasCategory ? category : null,
                Description = hasDescription ? description : null,
                AffectedFiles = affectedFiles.ToList(),
                HumanActionNeeded = hasHumanAction ? humanAction : null
            };

        var feedbackGen =
            from outcome in Gen.Elements(FeedbackOutcome.Success, FeedbackOutcome.Failure)
            from collectedAt in Gen.Elements(
                new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 15, 8, 30, 0, DateTimeKind.Utc),
                new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                new DateTime(2026, 3, 10, 14, 22, 33, DateTimeKind.Utc),
                new DateTime(2024, 7, 4, 0, 0, 0, DateTimeKind.Utc))
            from harness in harnessFeedbackGen
            from hasIssue in Gen.Elements(true, false)
            from issue in issueFeedbackGen
            select new RunFeedback
            {
                Outcome = outcome,
                CollectedAtUtc = collectedAt,
                Harness = harness,
                Issue = hasIssue ? issue : null
            };

        return feedbackGen.ToArbitrary();
    }
}
