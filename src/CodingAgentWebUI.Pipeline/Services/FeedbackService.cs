using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Stateless service responsible for parsing agent responses into <see cref="RunFeedback"/> objects,
/// applying validation/truncation, and providing fallback records when parsing fails.
/// </summary>
public sealed class FeedbackService
{
    private readonly ILogger _logger;

    public FeedbackService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Loads distinct harness and issue category labels from the most recent run summaries.
    /// Returns empty lists if the history service is not available or an error occurs.
    /// </summary>
    public async Task<(IReadOnlyList<string> HarnessCategories, IReadOnlyList<string> IssueCategories)> LoadPreviousCategoriesAsync(
        IPipelineRunHistoryService? historyService, CancellationToken ct = default)
    {
        if (historyService is null)
            return ([], []);

        try
        {
            var allSummaries = await historyService.GetRunHistoryAsync(ct).ConfigureAwait(false);
            var recentSummaries = allSummaries
                // TODO: Add fallback for legacy summaries where StartedAtOffset == default (consistent with PipelineRunHistoryService)
                .OrderByDescending(s => s.StartedAtOffset)
                .Take(FeedbackConstraints.MaxRecentRunsForCategories)
                .ToList();

            var harnessCategories = recentSummaries
                .Where(s => s.Feedback?.Harness.Category is not null)
                .Select(s => s.Feedback!.Harness.Category!)
                .Distinct()
                .ToList();

            var issueCategories = recentSummaries
                .Where(s => s.Feedback?.Issue?.Category is not null)
                .Select(s => s.Feedback!.Issue!.Category!)
                .Distinct()
                .ToList();

            return (harnessCategories, issueCategories);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load previous feedback categories, using empty lists");
            return ([], []);
        }
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

        var jsonBlock = JsonBlockExtractor.Extract(responseText, LooksLikeFeedbackJson);

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
        var dto = JsonSerializer.Deserialize<FeedbackDto>(json, PipelineJsonOptions.Lenient);

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

            var (category, stuckReason, missingContext, missingCapabilities, promptIssues, suggestions)
                = ExtractHarnessFields(root);
            var (hasIssue, issueCategory, issueDescription, affectedFiles, humanActionNeeded)
                = ExtractIssueFields(root);

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
    /// Extracts harness fields from the JSON root element, falling back to the root if
    /// no "harness"/"Harness" property is found (lenient root-level extraction).
    /// </summary>
    private static (string? category, string? stuckReason, List<string> missingContext,
        List<string> missingCapabilities, List<string> promptIssues, List<string> suggestions)
        ExtractHarnessFields(JsonElement root)
    {
        var source = (root.TryGetProperty("harness", out var harnessElement)
                      || root.TryGetProperty("Harness", out harnessElement))
            ? harnessElement
            : root;

        return (
            TryGetString(source, "category", "Category"),
            TryGetString(source, "stuckReason", "StuckReason", "stuck_reason"),
            TryGetStringList(source, "missingContext", "MissingContext", "missing_context"),
            TryGetStringList(source, "missingCapabilities", "MissingCapabilities", "missing_capabilities"),
            TryGetStringList(source, "promptIssues", "PromptIssues", "prompt_issues"),
            TryGetStringList(source, "suggestions", "Suggestions")
        );
    }

    /// <summary>
    /// Extracts issue feedback fields from the JSON root element.
    /// Returns hasIssue=false if no "issue"/"Issue" property is found.
    /// </summary>
    private static (bool hasIssue, string? issueCategory, string? issueDescription,
        List<string> affectedFiles, string? humanActionNeeded)
        ExtractIssueFields(JsonElement root)
    {
        if (!root.TryGetProperty("issue", out var issueElement) &&
            !root.TryGetProperty("Issue", out issueElement))
            return (false, null, null, [], null);

        var issueCategory = TryGetString(issueElement, "category", "Category");
        var issueDescription = TryGetString(issueElement, "description", "Description");
        var affectedFiles = TryGetStringList(issueElement, "affectedFiles", "AffectedFiles", "affected_files");
        var humanActionNeeded = TryGetString(issueElement, "humanActionNeeded", "HumanActionNeeded", "human_action_needed");
        var hasIssue = issueCategory is not null || issueDescription is not null ||
                       affectedFiles.Count > 0 || humanActionNeeded is not null;

        return (hasIssue, issueCategory, issueDescription, affectedFiles, humanActionNeeded);
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
        if (items.Count > maxItems)
        {
            _logger.Warning("Truncating {FieldName} from {OriginalCount} to {MaxCount} items",
                fieldName, items.Count, maxItems);
            items = items.Take(maxItems).ToList();
        }

        return items
            .Select((item, i) =>
            {
                if (item.Length <= maxStringLength) return item;
                _logger.Warning("Truncating {FieldName}[{Index}] from {OriginalLength} to {MaxLength} characters",
                    fieldName, i, item.Length, maxStringLength);
                return item[..maxStringLength];
            })
            .ToList();
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
