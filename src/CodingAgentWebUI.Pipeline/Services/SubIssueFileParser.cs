using System.Text.Json;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Parses sub-issue JSON files from the workspace and validates schema.
/// </summary>
public static class SubIssueFileParser
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads all JSON files from the sub-issues directory, validates schema,
    /// and returns valid proposals in alphabetical file-name order.
    /// Invalid files are logged and skipped.
    /// </summary>
    public static async Task<IReadOnlyList<SubIssueProposal>> ParseSubIssueFilesAsync(
        string workspacePath, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(logger);

        var subIssuesDir = Path.Combine(workspacePath, ".agent", "sub-issues");

        if (!Directory.Exists(subIssuesDir))
        {
            logger.Warning("Sub-issues directory does not exist: {Directory}", subIssuesDir);
            return [];
        }

        var files = Directory.GetFiles(subIssuesDir, "*.json");
        if (files.Length == 0)
        {
            logger.Warning("No JSON files found in sub-issues directory: {Directory}", subIssuesDir);
            return [];
        }

        // Sort by filename (alphabetical) to ensure deterministic ordering
        Array.Sort(files, (a, b) => string.Compare(
            Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

        var proposals = new List<SubIssueProposal>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);

            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var proposal = ParseAndValidate(json, fileName, logger);

                if (proposal is not null)
                {
                    proposals.Add(proposal);
                }
            }
            catch (IOException ex)
            {
                logger.Warning("Failed to read sub-issue file {FileName}: {Error}", fileName, ex.Message);
            }
        }

        return proposals;
    }

    private static SubIssueProposal? ParseAndValidate(string json, string fileName, ILogger logger)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.Warning("Invalid JSON in sub-issue file {FileName}: {Error}", fileName, ex.Message);
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                logger.Warning("Sub-issue file {FileName} is not a JSON object", fileName);
                return null;
            }

            var title = ReadRequiredString(root, fileName, "title", logger);
            if (title is null) return null;

            var body = ReadRequiredString(root, fileName, "body", logger);
            if (body is null) return null;

            var dependencies = ReadStringArray(root, fileName, "dependencies", logger);
            if (dependencies is null) return null;

            var labels = ReadStringArray(root, fileName, "labels", logger);
            if (labels is null) return null;

            var targetRepository = ReadOptionalString(root, fileName, "targetRepository", logger);

            return new SubIssueProposal
            {
                Title = title,
                Body = body,
                Dependencies = dependencies,
                Labels = labels,
                TargetRepository = targetRepository
            };
        }
    }

    /// <summary>
    /// Reads a required non-empty string property from a JSON object (case-insensitive: lower then Title).
    /// Logs a warning and returns null when the property is missing, wrong type, or empty.
    /// TODO: [WARNING] No test exercises a JSON input where a required field (e.g. 'title') is present
    /// but whitespace-only, verifying the "has empty '{Field}'" warning is logged and null is returned.
    /// Add a unit test for SubIssueFileParser with whitespace-only field values to lock in this behavior.
    /// </summary>
    private static string? ReadRequiredString(JsonElement root, string fileName, string propertyName, ILogger logger)
    {
        var titleCased = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (!root.TryGetProperty(propertyName, out var element) &&
            !root.TryGetProperty(titleCased, out element))
        {
            logger.Warning("Sub-issue file {FileName} is missing required field '{Field}'", fileName, propertyName);
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            logger.Warning("Sub-issue file {FileName} has '{Field}' with incorrect type (expected string)", fileName, propertyName);
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            logger.Warning("Sub-issue file {FileName} has empty '{Field}'", fileName, propertyName);
            return null;
        }

        return value;
    }

    /// <summary>
    /// Reads a required string-array property from a JSON object (case-insensitive: lower then Title).
    /// Logs a warning and returns null when missing, wrong type, or contains non-string elements.
    /// </summary>
    private static List<string>? ReadStringArray(JsonElement root, string fileName, string propertyName, ILogger logger)
    {
        var titleCased = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (!root.TryGetProperty(propertyName, out var element) &&
            !root.TryGetProperty(titleCased, out element))
        {
            logger.Warning("Sub-issue file {FileName} is missing required field '{Field}'", fileName, propertyName);
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            logger.Warning("Sub-issue file {FileName} has '{Field}' with incorrect type (expected array)", fileName, propertyName);
            return null;
        }

        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                logger.Warning("Sub-issue file {FileName} has non-string element in '{Field}' array", fileName, propertyName);
                return null;
            }
            items.Add(item.GetString()!);
        }

        return items;
    }

    /// <summary>
    /// Reads an optional string property (case-insensitive: lower then Title).
    /// Returns null when missing, null-valued, or not a string (logs a warning for wrong type).
    /// </summary>
    private static string? ReadOptionalString(JsonElement root, string fileName, string propertyName, ILogger logger)
    {
        var titleCased = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (!root.TryGetProperty(propertyName, out var element) &&
            !root.TryGetProperty(titleCased, out element))
            return null;

        if (element.ValueKind == JsonValueKind.Null)
            return null;

        if (element.ValueKind != JsonValueKind.String)
        {
            logger.Warning("Sub-issue file {FileName} has '{Field}' with incorrect type (expected string or null), ignoring", fileName, propertyName);
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
