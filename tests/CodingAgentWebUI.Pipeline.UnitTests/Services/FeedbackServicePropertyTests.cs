// Feature: 020-agent-feedback-loops, Property 2: Truncation preserves constraints
// Feature: 020-agent-feedback-loops, Property 4: Parsing extracts feedback from embedded JSON
// Feature: 020-agent-feedback-loops, Property 10: Partial JSON parsing extracts valid fields
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for FeedbackService parsing behavior.
/// </summary>
public class FeedbackServicePropertyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ILogger NullLogger = new LoggerConfiguration().CreateLogger();

    // Feature: 020-agent-feedback-loops, Property 2: Truncation preserves constraints
    /// <summary>
    /// Property 2: Truncation Preserves Constraints
    /// For any RunFeedback with oversized fields, after truncation:
    /// - All Category fields ≤ 50 chars
    /// - All other string fields ≤ 500 chars
    /// - MissingContext ≤ 5 items, MissingCapabilities ≤ 5, PromptIssues ≤ 5, Suggestions ≤ 3, AffectedFiles ≤ 5
    /// - Truncated strings are a prefix of the original
    /// - Truncated lists are a prefix of the original (same order, same first N items)
    /// **Validates: Requirements 1.6, 1.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(OversizedRunFeedbackArbitraries) })]
    public void ApplyTruncation_EnforcesAllConstraints(OversizedFeedbackInput input)
    {
        var logger = new Mock<ILogger>();
        var service = new FeedbackService(logger.Object);

        var result = service.ApplyTruncation(input.Feedback, input.Outcome);

        // --- String length constraints ---

        // Harness.Category ≤ MaxCategoryLength (50)
        if (result.Harness.Category is not null)
        {
            result.Harness.Category.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxCategoryLength);
        }

        // Harness.StuckReason ≤ MaxStringLength (500)
        if (result.Harness.StuckReason is not null)
        {
            result.Harness.StuckReason.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
        }

        // All items in list fields ≤ MaxStringLength (500)
        foreach (var item in result.Harness.MissingContext)
            item.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
        foreach (var item in result.Harness.MissingCapabilities)
            item.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
        foreach (var item in result.Harness.PromptIssues)
            item.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
        foreach (var item in result.Harness.Suggestions)
            item.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);

        // Issue fields (when present)
        if (result.Issue is not null)
        {
            if (result.Issue.Category is not null)
                result.Issue.Category.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxCategoryLength);
            if (result.Issue.Description is not null)
                result.Issue.Description.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
            if (result.Issue.HumanActionNeeded is not null)
                result.Issue.HumanActionNeeded.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
            foreach (var item in result.Issue.AffectedFiles)
                item.Length.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxStringLength);
        }

        // --- List count constraints ---

        result.Harness.MissingContext.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxMissingContextItems);
        result.Harness.MissingCapabilities.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxMissingCapabilitiesItems);
        result.Harness.PromptIssues.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxPromptIssuesItems);
        result.Harness.Suggestions.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxSuggestionsItems);

        if (result.Issue is not null)
        {
            result.Issue.AffectedFiles.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxAffectedFilesItems);
        }

        // --- Truncated strings are a prefix of the original ---

        AssertStringIsPrefix(input.Feedback.Harness.Category, result.Harness.Category);
        AssertStringIsPrefix(input.Feedback.Harness.StuckReason, result.Harness.StuckReason);

        // For list items that survived truncation, each string is a prefix of the original
        AssertListItemStringsArePrefix(input.Feedback.Harness.MissingContext, result.Harness.MissingContext);
        AssertListItemStringsArePrefix(input.Feedback.Harness.MissingCapabilities, result.Harness.MissingCapabilities);
        AssertListItemStringsArePrefix(input.Feedback.Harness.PromptIssues, result.Harness.PromptIssues);
        AssertListItemStringsArePrefix(input.Feedback.Harness.Suggestions, result.Harness.Suggestions);

        if (input.Feedback.Issue is not null && result.Issue is not null)
        {
            AssertStringIsPrefix(input.Feedback.Issue.Category, result.Issue.Category);
            AssertStringIsPrefix(input.Feedback.Issue.Description, result.Issue.Description);
            AssertStringIsPrefix(input.Feedback.Issue.HumanActionNeeded, result.Issue.HumanActionNeeded);
            AssertListItemStringsArePrefix(input.Feedback.Issue.AffectedFiles, result.Issue.AffectedFiles);
        }

        // --- Truncated lists are a prefix of the original (same order, same first N items) ---

        AssertListOrderPreserved(input.Feedback.Harness.MissingContext, result.Harness.MissingContext);
        AssertListOrderPreserved(input.Feedback.Harness.MissingCapabilities, result.Harness.MissingCapabilities);
        AssertListOrderPreserved(input.Feedback.Harness.PromptIssues, result.Harness.PromptIssues);
        AssertListOrderPreserved(input.Feedback.Harness.Suggestions, result.Harness.Suggestions);

        if (input.Feedback.Issue is not null && result.Issue is not null)
        {
            AssertListOrderPreserved(input.Feedback.Issue.AffectedFiles, result.Issue.AffectedFiles);
        }
    }

    /// <summary>
    /// Asserts that the truncated string is a prefix of the original (or both are null).
    /// </summary>
    private static void AssertStringIsPrefix(string? original, string? truncated)
    {
        if (original is null || truncated is null)
            return;

        original.Should().StartWith(truncated);
    }

    /// <summary>
    /// Asserts that each item in the truncated list is a prefix of the corresponding item in the original list.
    /// </summary>
    private static void AssertListItemStringsArePrefix(IReadOnlyList<string> original, IReadOnlyList<string> truncated)
    {
        for (var i = 0; i < truncated.Count; i++)
        {
            original[i].Should().StartWith(truncated[i],
                $"item at index {i} in truncated list should be a prefix of the original");
        }
    }

    /// <summary>
    /// Asserts that the truncated list is a prefix of the original list (same order, first N items correspond).
    /// </summary>
    private static void AssertListOrderPreserved(IReadOnlyList<string> original, IReadOnlyList<string> truncated)
    {
        truncated.Count.Should().BeLessThanOrEqualTo(original.Count,
            "truncated list should not have more items than the original");

        // The truncated list items correspond to the first N items of the original (in order)
        for (var i = 0; i < truncated.Count; i++)
        {
            original[i].Should().StartWith(truncated[i]);
        }
    }

    /// <summary>
    /// Property 4: Parsing Extracts Feedback from Embedded JSON
    /// For any valid RunFeedback JSON block embedded within arbitrary surrounding text
    /// (markdown code fences, prose, whitespace), ParseFeedbackFromResponse extracts
    /// and returns matching fields (after truncation).
    /// **Validates: Requirements 2.7, 3.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(EmbeddedFeedbackArbitraries) })]
    public void ParseFeedbackFromResponse_ExtractsFeedbackFromEmbeddedJson(EmbeddedFeedbackTestCase testCase)
    {
        var service = new FeedbackService(NullLogger);

        var result = service.ParseFeedbackFromResponse(
            testCase.WrappedText,
            testCase.Outcome,
            testCase.CollectedAtUtc);

        result.Should().NotBeNull();
        result.Outcome.Should().Be(testCase.Outcome);
        result.CollectedAtUtc.Should().Be(testCase.CollectedAtUtc);

        // Harness fields should match (after truncation)
        var expectedCategory = Truncate(testCase.OriginalHarness.Category, FeedbackConstraints.MaxCategoryLength);
        var expectedStuckReason = Truncate(testCase.OriginalHarness.StuckReason, FeedbackConstraints.MaxStringLength);

        // For Failure outcome with no StuckReason, the service enforces a default
        if (testCase.Outcome == FeedbackOutcome.Failure && string.IsNullOrEmpty(expectedStuckReason))
        {
            result.Harness.StuckReason.Should().NotBeNullOrEmpty();
        }
        else
        {
            result.Harness.Category.Should().Be(expectedCategory);
            result.Harness.StuckReason.Should().Be(expectedStuckReason);
        }

        // List fields: truncated to max items, each item truncated to max string length
        AssertListPrefix(
            result.Harness.MissingContext,
            testCase.OriginalHarness.MissingContext,
            FeedbackConstraints.MaxMissingContextItems);

        AssertListPrefix(
            result.Harness.MissingCapabilities,
            testCase.OriginalHarness.MissingCapabilities,
            FeedbackConstraints.MaxMissingCapabilitiesItems);

        AssertListPrefix(
            result.Harness.PromptIssues,
            testCase.OriginalHarness.PromptIssues,
            FeedbackConstraints.MaxPromptIssuesItems);

        AssertListPrefix(
            result.Harness.Suggestions,
            testCase.OriginalHarness.Suggestions,
            FeedbackConstraints.MaxSuggestionsItems);

        // Issue feedback
        if (testCase.OriginalIssue is null)
        {
            result.Issue.Should().BeNull();
        }
        else
        {
            result.Issue.Should().NotBeNull();
            var expectedIssueCategory = Truncate(testCase.OriginalIssue.Category, FeedbackConstraints.MaxCategoryLength);
            var expectedDescription = Truncate(testCase.OriginalIssue.Description, FeedbackConstraints.MaxStringLength);
            var expectedHumanAction = Truncate(testCase.OriginalIssue.HumanActionNeeded, FeedbackConstraints.MaxStringLength);

            result.Issue!.Category.Should().Be(expectedIssueCategory);
            result.Issue.Description.Should().Be(expectedDescription);
            result.Issue.HumanActionNeeded.Should().Be(expectedHumanAction);

            AssertListPrefix(
                result.Issue.AffectedFiles,
                testCase.OriginalIssue.AffectedFiles,
                FeedbackConstraints.MaxAffectedFilesItems);
        }
    }

    // Feature: 020-agent-feedback-loops, Property 10: Partial JSON parsing extracts valid fields
    /// <summary>
    /// Property 10: Partial JSON Parsing Extracts Valid Fields
    /// For any JSON string that contains some valid feedback fields and some invalid/missing fields,
    /// FeedbackService.ParseFeedbackFromResponse() SHALL return a RunFeedback that preserves the
    /// successfully parsed fields rather than returning a completely empty fallback.
    /// **Validates: Requirements 8.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PartialFeedbackJsonArbitraries) })]
    public void ParseFeedbackFromResponse_WithPartiallyValidJson_PreservesValidFields(PartialFeedbackTestCase testCase)
    {
        // Arrange
        var service = new FeedbackService(NullLogger);
        var outcome = FeedbackOutcome.Success;
        var collectedAt = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        // Wrap the partial JSON in a fenced code block so ExtractJsonBlock finds it
        var responseText = $"Here is my feedback:\n```json\n{testCase.Json}\n```\nEnd of response.";

        // Act
        var result = service.ParseFeedbackFromResponse(responseText, outcome, collectedAt);

        // Assert — the valid category field is preserved (not a completely empty fallback)
        // The input always has a valid harness.category string field, so it should be extracted
        // via the AttemptPartialParse path (since full deserialization fails due to invalid fields)
        result.Should().NotBeNull();
        result.Harness.Category.Should().NotBeNullOrEmpty(
            because: "a valid harness.category field was present in the JSON and should be preserved by partial parsing");

        // The extracted category should match the input category (possibly truncated to MaxCategoryLength)
        var expectedCategory = testCase.ExpectedCategory.Length > FeedbackConstraints.MaxCategoryLength
            ? testCase.ExpectedCategory[..FeedbackConstraints.MaxCategoryLength]
            : testCase.ExpectedCategory;
        result.Harness.Category.Should().Be(expectedCategory);

        // Verify that valid suggestions (when present as a proper string array) are also preserved
        if (testCase.ExpectedSuggestions is not null)
        {
            result.Harness.Suggestions.Should().NotBeEmpty(
                because: "valid suggestions array entries should be preserved by partial parsing");

            var maxSuggestions = Math.Min(testCase.ExpectedSuggestions.Count, FeedbackConstraints.MaxSuggestionsItems);
            result.Harness.Suggestions.Count.Should().BeLessThanOrEqualTo(FeedbackConstraints.MaxSuggestionsItems);

            for (var i = 0; i < Math.Min(result.Harness.Suggestions.Count, maxSuggestions); i++)
            {
                var expected = testCase.ExpectedSuggestions[i].Length > FeedbackConstraints.MaxStringLength
                    ? testCase.ExpectedSuggestions[i][..FeedbackConstraints.MaxStringLength]
                    : testCase.ExpectedSuggestions[i];
                result.Harness.Suggestions[i].Should().Be(expected);
            }
        }

        // The result should NOT be a completely empty fallback — at minimum category is preserved
        var isCompletelyEmpty = result.Harness.Category is null
            && result.Harness.StuckReason is null
            && result.Harness.MissingContext.Count == 0
            && result.Harness.MissingCapabilities.Count == 0
            && result.Harness.PromptIssues.Count == 0
            && result.Harness.Suggestions.Count == 0;

        isCompletelyEmpty.Should().BeFalse(
            because: "partial parsing should preserve valid fields rather than returning a completely empty fallback");
    }

    private static void AssertListPrefix(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> original,
        int maxItems)
    {
        var expectedCount = Math.Min(original.Count, maxItems);
        actual.Should().HaveCount(expectedCount);

        for (var i = 0; i < expectedCount; i++)
        {
            var expectedItem = Truncate(original[i], FeedbackConstraints.MaxStringLength);
            actual[i].Should().Be(expectedItem);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

/// <summary>
/// Test case record for the embedded JSON parsing property test.
/// </summary>
public sealed class EmbeddedFeedbackTestCase
{
    public required string WrappedText { get; init; }
    public required FeedbackOutcome Outcome { get; init; }
    public required DateTime CollectedAtUtc { get; init; }
    public required HarnessFeedback OriginalHarness { get; init; }
    public required IssueFeedback? OriginalIssue { get; init; }
}

/// <summary>
/// FsCheck arbitrary generators for embedded feedback JSON property tests.
/// Generates valid feedback JSON blocks embedded in various wrappers:
/// - Markdown fenced code blocks (```json ... ```)
/// - Surrounded by prose text
/// - With leading/trailing whitespace
/// </summary>
public class EmbeddedFeedbackArbitraries
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] CategoryPool =
    [
        "missing file context",
        "mcp tool timeout",
        "prompt instruction gap",
        "contradictory criteria",
        "missing component"
    ];

    private static readonly string[] StuckReasonPool =
    [
        "Could not find the referenced file src/main.ts",
        "MCP server timed out after 30 seconds",
        "The prompt said to use React but the project uses Vue",
        "Build failed due to missing dependency"
    ];

    private static readonly string[] ContextPool =
    [
        "tsconfig.json was needed",
        "The .env file was not provided",
        "Database schema was missing",
        "API documentation not available"
    ];

    private static readonly string[] CapabilityPool =
    [
        "Run integration tests",
        "Access to database",
        "Browser preview",
        "Git blame history"
    ];

    private static readonly string[] PromptIssuePool =
    [
        "Prompt says use latest version but unspecified",
        "Contradictory instructions about error handling",
        "Missing context about project architecture"
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
        "package.json"
    ];

    private static readonly string[] DescriptionPool =
    [
        "Issue acceptance criteria are contradictory",
        "Referenced component does not exist in the repo",
        "Pre-existing bug in the area that needs modification"
    ];

    private static readonly string[] HumanActionPool =
    [
        "Fix the contradictory acceptance criteria",
        "Add the missing component referenced in the issue",
        "Update the README with correct setup instructions"
    ];

    private static readonly string[] ProsePool =
    [
        "Here is my analysis of the run:\n",
        "After reviewing the pipeline execution, I have the following feedback:\n\n",
        "The run completed. Below is my structured feedback.\n\n",
        "I encountered several issues during this run. ",
        "## Summary\n\nThe pipeline had some difficulties.\n\n"
    ];

    private static readonly string[] TrailingProsePool =
    [
        "\n\nLet me know if you need more details.",
        "\n\nI hope this helps improve the pipeline.",
        "\n\nEnd of feedback.",
        "",
        "\n"
    ];

    public static Arbitrary<EmbeddedFeedbackTestCase> EmbeddedFeedbackTestCaseArb()
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

        var wrapperGen = Gen.Elements(
            WrapperKind.MarkdownFenced,
            WrapperKind.ProseWrapped,
            WrapperKind.LeadingTrailingWhitespace);

        var testCaseGen =
            from outcome in Gen.Elements(FeedbackOutcome.Success, FeedbackOutcome.Failure)
            from collectedAt in Gen.Elements(
                new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2025, 6, 15, 8, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 10, 14, 22, 33, DateTimeKind.Utc))
            from harness in harnessFeedbackGen
            from hasIssue in Gen.Elements(true, false)
            from issue in issueFeedbackGen
            from wrapper in wrapperGen
            from leadingProse in Gen.Elements(ProsePool)
            from trailingProse in Gen.Elements(TrailingProsePool)
            let feedbackDto = new FeedbackJsonDto
            {
                Harness = new HarnessJsonDto
                {
                    Category = harness.Category,
                    StuckReason = harness.StuckReason,
                    MissingContext = harness.MissingContext.ToList(),
                    MissingCapabilities = harness.MissingCapabilities.ToList(),
                    PromptIssues = harness.PromptIssues.ToList(),
                    Suggestions = harness.Suggestions.ToList()
                },
                Issue = hasIssue
                    ? new IssueJsonDto
                    {
                        Category = issue.Category,
                        Description = issue.Description,
                        AffectedFiles = issue.AffectedFiles.ToList(),
                        HumanActionNeeded = issue.HumanActionNeeded
                    }
                    : null
            }
            let json = JsonSerializer.Serialize(feedbackDto, JsonOptions)
            let wrappedText = WrapJson(json, wrapper, leadingProse, trailingProse)
            select new EmbeddedFeedbackTestCase
            {
                WrappedText = wrappedText,
                Outcome = outcome,
                CollectedAtUtc = collectedAt,
                OriginalHarness = harness,
                OriginalIssue = hasIssue ? issue : null
            };

        return testCaseGen.ToArbitrary();
    }

    private static string WrapJson(string json, WrapperKind wrapper, string leadingProse, string trailingProse)
    {
        return wrapper switch
        {
            WrapperKind.MarkdownFenced =>
                $"{leadingProse}```json\n{json}\n```{trailingProse}",
            WrapperKind.ProseWrapped =>
                $"{leadingProse}{json}{trailingProse}",
            WrapperKind.LeadingTrailingWhitespace =>
                $"   \n\n  {leadingProse}```json\n{json}\n```  \n\n  {trailingProse}",
            _ => json
        };
    }

    private enum WrapperKind
    {
        MarkdownFenced,
        ProseWrapped,
        LeadingTrailingWhitespace
    }

    /// <summary>DTO for serializing feedback JSON in the format the agent would produce.</summary>
    private sealed class FeedbackJsonDto
    {
        public HarnessJsonDto? Harness { get; set; }
        public IssueJsonDto? Issue { get; set; }
    }

    private sealed class HarnessJsonDto
    {
        public string? Category { get; set; }
        public string? StuckReason { get; set; }
        public List<string>? MissingContext { get; set; }
        public List<string>? MissingCapabilities { get; set; }
        public List<string>? PromptIssues { get; set; }
        public List<string>? Suggestions { get; set; }
    }

    private sealed class IssueJsonDto
    {
        public string? Category { get; set; }
        public string? Description { get; set; }
        public List<string>? AffectedFiles { get; set; }
        public string? HumanActionNeeded { get; set; }
    }
}

/// <summary>
/// Test case record for the partial JSON parsing property test (Property 10).
/// Contains JSON with valid category + invalid fields that trigger partial parse.
/// </summary>
public sealed class PartialFeedbackTestCase
{
    public required string Json { get; init; }
    public required string ExpectedCategory { get; init; }
    public required IReadOnlyList<string>? ExpectedSuggestions { get; init; }

    public override string ToString() => Json;
}

/// <summary>
/// FsCheck arbitrary generators for partial JSON parsing property tests.
/// Generates JSON objects that have:
/// - A valid harness.category field (string)
/// - At least one invalid field (wrong type for a list field) that causes full deserialization to fail
/// - Optionally a valid suggestions array to verify multiple valid fields are preserved
/// This forces FeedbackService into the AttemptPartialParse code path.
/// </summary>
public class PartialFeedbackJsonArbitraries
{
    private static readonly string[] ValidCategories =
    [
        "missing file context",
        "mcp tool timeout",
        "prompt instruction gap",
        "test environment issue",
        "dependency conflict",
        "compilation error",
        "network timeout",
        "auth failure",
        "config missing",
        "schema mismatch"
    ];

    private static readonly string[] ValidSuggestions =
    [
        "Include tsconfig.json in initial context",
        "Add timeout retry for MCP calls",
        "Clarify which framework version to target"
    ];

    /// <summary>
    /// Invalid field entries that cause System.Text.Json deserialization to fail for the HarnessDto,
    /// forcing FeedbackService into the AttemptPartialParse path using JsonDocument.
    /// These represent fields with wrong types (number/bool/object instead of array/string).
    /// </summary>
    private static readonly string[] InvalidFieldVariants =
    [
        // missingContext as a number instead of array of strings
        "\"missingContext\": 42",
        // missingContext as a boolean instead of array of strings
        "\"missingContext\": true",
        // missingCapabilities as a number instead of array of strings
        "\"missingCapabilities\": 999",
        // promptIssues as a string instead of array of strings
        "\"promptIssues\": \"not an array\"",
        // stuckReason as an array instead of string
        "\"stuckReason\": [1, 2, 3]",
        // missingContext as an object instead of array
        "\"missingContext\": { \"invalid\": true }",
        // missingCapabilities as a nested object
        "\"missingCapabilities\": { \"tools\": [\"a\", \"b\"] }"
    ];

    public static Arbitrary<PartialFeedbackTestCase> PartialFeedbackTestCases()
    {
        var categoryGen = Gen.Elements(ValidCategories);
        var invalidFieldGen = Gen.Elements(InvalidFieldVariants);

        // Optionally include valid suggestions alongside the invalid fields
        var suggestionsGen =
            from includeSuggestions in Gen.Elements(true, false)
            from suggestions in Gen.SubListOf(ValidSuggestions)
            select includeSuggestions && suggestions.Count > 0 ? suggestions.ToList() : null;

        // Generate 1-2 invalid fields (using distinct to avoid duplicate JSON keys)
        var invalidFieldsGen =
            from count in Gen.Choose(1, 2)
            from fields in Gen.ArrayOf(invalidFieldGen, count)
            select fields.Distinct().ToArray();

        var gen =
            from category in categoryGen
            from invalidFields in invalidFieldsGen
            from suggestions in suggestionsGen
            select BuildPartialTestCase(category, invalidFields, suggestions);

        return gen.ToArbitrary();
    }

    private static PartialFeedbackTestCase BuildPartialTestCase(
        string category,
        string[] invalidFields,
        List<string>? suggestions)
    {
        // Escape the category for JSON
        var escapedCategory = category.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Build the harness fields: valid category + invalid fields + optional valid suggestions
        var fields = new List<string>
        {
            $"\"category\": \"{escapedCategory}\""
        };

        fields.AddRange(invalidFields);

        if (suggestions is not null && suggestions.Count > 0)
        {
            var suggestionsJson = "[" + string.Join(", ", suggestions.Select(s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"")) + "]";
            fields.Add($"\"suggestions\": {suggestionsJson}");
        }

        var harnessContent = string.Join(",\n      ", fields);

        var json = $$"""
        {
          "harness": {
            {{harnessContent}}
          }
        }
        """;

        return new PartialFeedbackTestCase
        {
            Json = json,
            ExpectedCategory = category,
            ExpectedSuggestions = suggestions
        };
    }
}


/// <summary>
/// Wrapper type for FsCheck to generate oversized RunFeedback instances paired with an outcome.
/// </summary>
public sealed class OversizedFeedbackInput
{
    public required RunFeedback Feedback { get; init; }
    public required FeedbackOutcome Outcome { get; init; }
}

/// <summary>
/// FsCheck arbitrary generators for oversized RunFeedback instances.
/// Generates RunFeedback with fields that EXCEED the maximum constraints:
/// - Strings > 500 characters (or > 50 for Category)
/// - Lists > max item count
/// This ensures the truncation logic is exercised.
/// </summary>
public class OversizedRunFeedbackArbitraries
{
    /// <summary>
    /// Generates a string of exactly the specified length using alphanumeric characters.
    /// </summary>
    private static Gen<string> GenStringOfLength(int length)
    {
        return Gen.ArrayOf(Gen.Elements(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ".ToCharArray()), length)
            .Select(chars => new string(chars));
    }

    /// <summary>
    /// Generates a string with length between min and max (inclusive).
    /// </summary>
    private static Gen<string> GenStringBetween(int minLength, int maxLength)
    {
        return Gen.Choose(minLength, maxLength).SelectMany(len => GenStringOfLength(len));
    }

    /// <summary>
    /// Generates a list of strings with count between min and max, each string oversized.
    /// </summary>
    private static Gen<List<string>> GenOversizedList(int minItems, int maxItems, int minStringLen, int maxStringLen)
    {
        return Gen.Choose(minItems, maxItems).SelectMany(count =>
            Gen.ArrayOf(GenStringBetween(minStringLen, maxStringLen), count).Select(arr => arr.ToList()));
    }

    public static Arbitrary<OversizedFeedbackInput> OversizedFeedbackInputArb()
    {
        // Generate oversized Category strings (> 50 chars)
        var oversizedCategoryGen = GenStringBetween(51, 120);

        // Generate oversized string fields (> 500 chars)
        var oversizedStringGen = GenStringBetween(501, 800);

        // Generate harness with oversized fields
        var harnessFeedbackGen =
            from category in oversizedCategoryGen
            from stuckReason in oversizedStringGen
            from missingContext in GenOversizedList(6, 10, 501, 700)
            from missingCapabilities in GenOversizedList(6, 10, 501, 700)
            from promptIssues in GenOversizedList(6, 10, 501, 700)
            from suggestions in GenOversizedList(4, 8, 501, 700)
            select new HarnessFeedback
            {
                Category = category,
                StuckReason = stuckReason,
                MissingContext = missingContext,
                MissingCapabilities = missingCapabilities,
                PromptIssues = promptIssues,
                Suggestions = suggestions
            };

        // Generate issue with oversized fields
        var issueFeedbackGen =
            from category in oversizedCategoryGen
            from description in oversizedStringGen
            from affectedFiles in GenOversizedList(6, 10, 501, 700)
            from humanActionNeeded in oversizedStringGen
            select new IssueFeedback
            {
                Category = category,
                Description = description,
                AffectedFiles = affectedFiles,
                HumanActionNeeded = humanActionNeeded
            };

        var feedbackGen =
            from outcome in Gen.Elements(FeedbackOutcome.Success, FeedbackOutcome.Failure)
            from collectedAt in Gen.Elements(
                new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc))
            from harness in harnessFeedbackGen
            from hasIssue in Gen.Elements(true, false)
            from issue in issueFeedbackGen
            select new OversizedFeedbackInput
            {
                Outcome = outcome,
                Feedback = new RunFeedback
                {
                    Outcome = outcome,
                    CollectedAtUtc = collectedAt,
                    Harness = harness,
                    Issue = hasIssue ? issue : null
                }
            };

        return feedbackGen.ToArbitrary();
    }
}
