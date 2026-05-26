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

            // Validate title: required, string, non-empty
            if (!root.TryGetProperty("title", out var titleElement) &&
                !root.TryGetProperty("Title", out titleElement))
            {
                logger.Warning("Sub-issue file {FileName} is missing required field 'title'", fileName);
                return null;
            }

            if (titleElement.ValueKind != JsonValueKind.String)
            {
                logger.Warning("Sub-issue file {FileName} has 'title' with incorrect type (expected string)", fileName);
                return null;
            }

            var title = titleElement.GetString();
            if (string.IsNullOrWhiteSpace(title))
            {
                logger.Warning("Sub-issue file {FileName} has empty 'title'", fileName);
                return null;
            }

            // Validate body: required, string, non-empty
            if (!root.TryGetProperty("body", out var bodyElement) &&
                !root.TryGetProperty("Body", out bodyElement))
            {
                logger.Warning("Sub-issue file {FileName} is missing required field 'body'", fileName);
                return null;
            }

            if (bodyElement.ValueKind != JsonValueKind.String)
            {
                logger.Warning("Sub-issue file {FileName} has 'body' with incorrect type (expected string)", fileName);
                return null;
            }

            var body = bodyElement.GetString();
            if (string.IsNullOrWhiteSpace(body))
            {
                logger.Warning("Sub-issue file {FileName} has empty 'body'", fileName);
                return null;
            }

            // Validate dependencies: required, array of strings
            if (!root.TryGetProperty("dependencies", out var depsElement) &&
                !root.TryGetProperty("Dependencies", out depsElement))
            {
                logger.Warning("Sub-issue file {FileName} is missing required field 'dependencies'", fileName);
                return null;
            }

            if (depsElement.ValueKind != JsonValueKind.Array)
            {
                logger.Warning("Sub-issue file {FileName} has 'dependencies' with incorrect type (expected array)", fileName);
                return null;
            }

            var dependencies = new List<string>();
            foreach (var dep in depsElement.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.String)
                {
                    logger.Warning("Sub-issue file {FileName} has non-string element in 'dependencies' array", fileName);
                    return null;
                }

                dependencies.Add(dep.GetString()!);
            }

            // Validate labels: required, array of strings
            if (!root.TryGetProperty("labels", out var labelsElement) &&
                !root.TryGetProperty("Labels", out labelsElement))
            {
                logger.Warning("Sub-issue file {FileName} is missing required field 'labels'", fileName);
                return null;
            }

            if (labelsElement.ValueKind != JsonValueKind.Array)
            {
                logger.Warning("Sub-issue file {FileName} has 'labels' with incorrect type (expected array)", fileName);
                return null;
            }

            var labels = new List<string>();
            foreach (var label in labelsElement.EnumerateArray())
            {
                if (label.ValueKind != JsonValueKind.String)
                {
                    logger.Warning("Sub-issue file {FileName} has non-string element in 'labels' array", fileName);
                    return null;
                }

                labels.Add(label.GetString()!);
            }

            return new SubIssueProposal
            {
                Title = title,
                Body = body,
                Dependencies = dependencies,
                Labels = labels
            };
        }
    }
}
