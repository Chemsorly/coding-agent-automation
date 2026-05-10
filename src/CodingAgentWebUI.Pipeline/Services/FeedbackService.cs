using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for parsing agent responses into <see cref="RunFeedback"/> objects,
/// applying validation/truncation, and providing fallback records when parsing fails.
/// </summary>
public sealed partial class FeedbackService
{
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Matches a fenced JSON code block: ```json ... ```
    [GeneratedRegex(@"```json\s*\n([\s\S]*?)\n\s*```", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FencedJsonBlockPattern();

    // Greedy match: captures from first { to last } in the response.
    // This is intentional — the fenced code block path handles 90%+ of cases.
    // When this path is used, LooksLikeFeedbackJson validates the candidate,
    // and AttemptPartialParse provides graceful degradation if it captures too much.
    [GeneratedRegex(@"\{[\s\S]*\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareJsonObjectPattern();

    public FeedbackService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Parses a <see cref="RunFeedback"/> from the agent's response text.
    /// Extracts the first JSON block matching the feedback schema.
    /// Applies truncation to oversized fields and logs warnings.
    /// Returns a degraded <see cref="RunFeedback"/> if parsing fails entirely.
    /// </summary>
    public RunFeedback ParseFeedbackFromResponse(
        string responseText,
        FeedbackOutcome outcome,
        DateTime collectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(responseText);

        var jsonBlock = ExtractJsonBlock(responseText);

        if (jsonBlock is null)
        {
            _logger.Warning("No JSON feedback block found in agent response ({Length} chars)", responseText.Length);
            return CreateFallbackForMissingJson(outcome, collectedAtUtc);
        }

        try
        {
            var feedback = DeserializeFeedback(jsonBlock, outcome, collectedAtUtc);
            return ApplyTruncation(feedback, outcome);
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Failed to deserialize feedback JSON, attempting partial parse. Raw JSON: {RawJson}", Truncate(jsonBlock, 1000));
            return AttemptPartialParse(jsonBlock, outcome, collectedAtUtc);
        }
    }

    /// <summary>
    /// Creates a fallback <see cref="RunFeedback"/> when the agent call fails or times out.
    /// </summary>
    public RunFeedback CreateFallbackFeedback(
        FeedbackOutcome outcome,
        string stuckReason,
        DateTime collectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stuckReason);

        return new RunFeedback
        {
            Outcome = outcome,
            CollectedAtUtc = collectedAtUtc,
            Harness = new HarnessFeedback
            {
                StuckReason = Truncate(stuckReason, FeedbackConstraints.MaxStringLength)
            },
            Issue = null
        };
    }

    /// <summary>
    /// Extracts the first JSON block from the response text.
    /// Prefers fenced code blocks (```json ... ```) over bare JSON objects.
    /// </summary>
    internal static string? ExtractJsonBlock(string responseText)
    {
        // Try fenced JSON block first
        var fencedMatch = FencedJsonBlockPattern().Match(responseText);
        if (fencedMatch.Success)
        {
            return fencedMatch.Groups[1].Value.Trim();
        }

        // Fall back to bare JSON object
        var bareMatch = BareJsonObjectPattern().Match(responseText);
        if (bareMatch.Success)
        {
            var candidate = bareMatch.Value;
            // Validate it looks like a feedback schema (has at least one expected field)
            if (LooksLikeFeedbackJson(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikeFeedbackJson(string json)
    {
        // Quick heuristic: check if the JSON contains at least one field name from the schema
        return json.Contains("harness", StringComparison.OrdinalIgnoreCase)
            || json.Contains("issue", StringComparison.OrdinalIgnoreCase)
            || json.Contains("category", StringComparison.OrdinalIgnoreCase)
            || json.Contains("stuckReason", StringComparison.OrdinalIgnoreCase)
            || json.Contains("stuck_reason", StringComparison.OrdinalIgnoreCase)
            || json.Contains("missingContext", StringComparison.OrdinalIgnoreCase)
            || json.Contains("missing_context", StringComparison.OrdinalIgnoreCase)
            || json.Contains("suggestions", StringComparison.OrdinalIgnoreCase);
    }

    private RunFeedback DeserializeFeedback(string json, FeedbackOutcome outcome, DateTime collectedAtUtc)
    {
        var dto = JsonSerializer.Deserialize<FeedbackDto>(json, JsonOptions);

        if (dto is null)
        {
            throw new JsonException("Deserialized feedback DTO is null");
        }

        var harness = new HarnessFeedback
        {
            Category = dto.Harness?.Category,
            StuckReason = dto.Harness?.StuckReason,
            MissingContext = dto.Harness?.MissingContext?.ToList() ?? [],
            MissingCapabilities = dto.Harness?.MissingCapabilities?.ToList() ?? [],
            PromptIssues = dto.Harness?.PromptIssues?.ToList() ?? [],
            Suggestions = dto.Harness?.Suggestions?.ToList() ?? []
        };

        IssueFeedback? issue = null;
        if (dto.Issue is not null)
        {
            issue = new IssueFeedback
            {
                Category = dto.Issue.Category,
                Description = dto.Issue.Description,
                AffectedFiles = dto.Issue.AffectedFiles?.ToList() ?? [],
                HumanActionNeeded = dto.Issue.HumanActionNeeded
            };
        }

        return new RunFeedback
        {
            Outcome = outcome,
            CollectedAtUtc = collectedAtUtc,
            Harness = harness,
            Issue = issue
        };
    }

    /// <summary>
    /// Attempts partial parsing using JsonDocument when full deserialization fails.
    /// Extracts whatever fields are valid and returns a degraded record.
    /// </summary>
    private RunFeedback AttemptPartialParse(string json, FeedbackOutcome outcome, DateTime collectedAtUtc)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? category = null;
            string? stuckReason = null;
            List<string> missingContext = [];
            List<string> missingCapabilities = [];
            List<string> promptIssues = [];
            List<string> suggestions = [];

            string? issueCategory = null;
            string? issueDescription = null;
            List<string> affectedFiles = [];
            string? humanActionNeeded = null;
            var hasIssue = false;

            // Try to extract harness fields
            if (root.TryGetProperty("harness", out var harnessElement) ||
                root.TryGetProperty("Harness", out harnessElement))
            {
                category = TryGetString(harnessElement, "category", "Category");
                stuckReason = TryGetString(harnessElement, "stuckReason", "StuckReason", "stuck_reason");
                missingContext = TryGetStringList(harnessElement, "missingContext", "MissingContext", "missing_context");
                missingCapabilities = TryGetStringList(harnessElement, "missingCapabilities", "MissingCapabilities", "missing_capabilities");
                promptIssues = TryGetStringList(harnessElement, "promptIssues", "PromptIssues", "prompt_issues");
                suggestions = TryGetStringList(harnessElement, "suggestions", "Suggestions");
            }
            else
            {
                // Fields might be at root level
                category = TryGetString(root, "category", "Category");
                stuckReason = TryGetString(root, "stuckReason", "StuckReason", "stuck_reason");
                missingContext = TryGetStringList(root, "missingContext", "MissingContext", "missing_context");
                missingCapabilities = TryGetStringList(root, "missingCapabilities", "MissingCapabilities", "missing_capabilities");
                promptIssues = TryGetStringList(root, "promptIssues", "PromptIssues", "prompt_issues");
                suggestions = TryGetStringList(root, "suggestions", "Suggestions");
            }

            // Try to extract issue fields
            if (root.TryGetProperty("issue", out var issueElement) ||
                root.TryGetProperty("Issue", out issueElement))
            {
                issueCategory = TryGetString(issueElement, "category", "Category");
                issueDescription = TryGetString(issueElement, "description", "Description");
                affectedFiles = TryGetStringList(issueElement, "affectedFiles", "AffectedFiles", "affected_files");
                humanActionNeeded = TryGetString(issueElement, "humanActionNeeded", "HumanActionNeeded", "human_action_needed");
                hasIssue = issueCategory is not null || issueDescription is not null ||
                           affectedFiles.Count > 0 || humanActionNeeded is not null;
            }

            var harness = new HarnessFeedback
            {
                Category = category,
                StuckReason = stuckReason,
                MissingContext = missingContext,
                MissingCapabilities = missingCapabilities,
                PromptIssues = promptIssues,
                Suggestions = suggestions
            };

            IssueFeedback? issue = hasIssue
                ? new IssueFeedback
                {
                    Category = issueCategory,
                    Description = issueDescription,
                    AffectedFiles = affectedFiles,
                    HumanActionNeeded = humanActionNeeded
                }
                : null;

            var feedback = new RunFeedback
            {
                Outcome = outcome,
                CollectedAtUtc = collectedAtUtc,
                Harness = harness,
                Issue = issue
            };

            return ApplyTruncation(feedback, outcome);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Partial JSON parse also failed, returning fallback");
            return CreateFallbackForMissingJson(outcome, collectedAtUtc);
        }
    }

    /// <summary>
    /// Applies truncation constraints to all fields of a <see cref="RunFeedback"/>.
    /// Also enforces the StuckReason requirement for Failure outcomes.
    /// </summary>
    internal RunFeedback ApplyTruncation(RunFeedback feedback, FeedbackOutcome outcome)
    {
        var harness = new HarnessFeedback
        {
            Category = TruncateField(feedback.Harness.Category, FeedbackConstraints.MaxCategoryLength, "Harness.Category"),
            StuckReason = TruncateField(feedback.Harness.StuckReason, FeedbackConstraints.MaxStringLength, "Harness.StuckReason"),
            MissingContext = TruncateList(feedback.Harness.MissingContext, FeedbackConstraints.MaxMissingContextItems, FeedbackConstraints.MaxStringLength, "Harness.MissingContext"),
            MissingCapabilities = TruncateList(feedback.Harness.MissingCapabilities, FeedbackConstraints.MaxMissingCapabilitiesItems, FeedbackConstraints.MaxStringLength, "Harness.MissingCapabilities"),
            PromptIssues = TruncateList(feedback.Harness.PromptIssues, FeedbackConstraints.MaxPromptIssuesItems, FeedbackConstraints.MaxStringLength, "Harness.PromptIssues"),
            Suggestions = TruncateList(feedback.Harness.Suggestions, FeedbackConstraints.MaxSuggestionsItems, FeedbackConstraints.MaxStringLength, "Harness.Suggestions")
        };

        // Enforce StuckReason for Failure outcome
        if (outcome == FeedbackOutcome.Failure && string.IsNullOrEmpty(harness.StuckReason))
        {
            harness = new HarnessFeedback
            {
                Category = harness.Category,
                StuckReason = "Agent did not produce structured feedback",
                MissingContext = harness.MissingContext,
                MissingCapabilities = harness.MissingCapabilities,
                PromptIssues = harness.PromptIssues,
                Suggestions = harness.Suggestions
            };
        }

        IssueFeedback? issue = null;
        if (feedback.Issue is not null)
        {
            issue = new IssueFeedback
            {
                Category = TruncateField(feedback.Issue.Category, FeedbackConstraints.MaxCategoryLength, "Issue.Category"),
                Description = TruncateField(feedback.Issue.Description, FeedbackConstraints.MaxStringLength, "Issue.Description"),
                AffectedFiles = TruncateList(feedback.Issue.AffectedFiles, FeedbackConstraints.MaxAffectedFilesItems, FeedbackConstraints.MaxStringLength, "Issue.AffectedFiles"),
                HumanActionNeeded = TruncateField(feedback.Issue.HumanActionNeeded, FeedbackConstraints.MaxStringLength, "Issue.HumanActionNeeded")
            };
        }

        return new RunFeedback
        {
            Outcome = outcome,
            CollectedAtUtc = feedback.CollectedAtUtc,
            Harness = harness,
            Issue = issue
        };
    }

    private RunFeedback CreateFallbackForMissingJson(FeedbackOutcome outcome, DateTime collectedAtUtc)
    {
        var stuckReason = outcome == FeedbackOutcome.Failure
            ? "Agent did not produce structured feedback"
            : null;

        return new RunFeedback
        {
            Outcome = outcome,
            CollectedAtUtc = collectedAtUtc,
            Harness = new HarnessFeedback
            {
                StuckReason = stuckReason
            },
            Issue = null
        };
    }

    private string? TruncateField(string? value, int maxLength, string fieldName)
    {
        if (value is null)
            return null;

        if (value.Length <= maxLength)
            return value;

        _logger.Warning("Truncating {FieldName} from {OriginalLength} to {MaxLength} characters",
            fieldName, value.Length, maxLength);
        return value[..maxLength];
    }

    private IReadOnlyList<string> TruncateList(IReadOnlyList<string> items, int maxItems, int maxStringLength, string fieldName)
    {
        var truncatedItems = items;

        if (items.Count > maxItems)
        {
            _logger.Warning("Truncating {FieldName} from {OriginalCount} to {MaxCount} items",
                fieldName, items.Count, maxItems);
            truncatedItems = items.Take(maxItems).ToList();
        }

        // Also truncate individual string items
        var result = new List<string>(truncatedItems.Count);
        for (var i = 0; i < truncatedItems.Count; i++)
        {
            var item = truncatedItems[i];
            if (item.Length > maxStringLength)
            {
                _logger.Warning("Truncating {FieldName}[{Index}] from {OriginalLength} to {MaxLength} characters",
                    fieldName, i, item.Length, maxStringLength);
                result.Add(item[..maxStringLength]);
            }
            else
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TryGetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    private static List<string> TryGetStringList(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in prop.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var str = item.GetString();
                        if (str is not null)
                            list.Add(str);
                    }
                }
                return list;
            }
        }
        return [];
    }

    /// <summary>
    /// Internal DTO for lenient deserialization of the agent's JSON feedback block.
    /// </summary>
    private sealed class FeedbackDto
    {
        public HarnessDto? Harness { get; set; }
        public IssueDto? Issue { get; set; }
    }

    private sealed class HarnessDto
    {
        public string? Category { get; set; }
        public string? StuckReason { get; set; }
        public List<string>? MissingContext { get; set; }
        public List<string>? MissingCapabilities { get; set; }
        public List<string>? PromptIssues { get; set; }
        public List<string>? Suggestions { get; set; }
    }

    private sealed class IssueDto
    {
        public string? Category { get; set; }
        public string? Description { get; set; }
        public List<string>? AffectedFiles { get; set; }
        public string? HumanActionNeeded { get; set; }
    }
}
