using System.Text;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Downloads open issues and writes them as markdown files to the workspace
/// for agent deduplication context. Reusable across pipeline phases.
/// Accepts <see cref="IAgentIssueOperations"/> (proxied through orchestrator) rather than
/// IIssueProvider directly, keeping the agent credential-free.
/// </summary>
public sealed class OpenIssueContextWriter : IOpenIssueContextWriter
{
    private readonly ILogger _logger;

    public OpenIssueContextWriter(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> WriteOpenIssueContextAsync(
        IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueOps);
        ArgumentNullException.ThrowIfNull(workspacePath);

        if (maxIssues < 1)
        {
            _logger.Warning("MaxIssues must be at least 1, received {MaxIssues}. Using 1.", maxIssues);
            maxIssues = 1;
        }

        var outputDir = Path.Combine(workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.CreateDirectory(outputDir);

        // Paginate through open issues to collect identifiers up to the cap
        var identifiers = await CollectIssueIdentifiersAsync(issueOps, maxIssues, ct);

        if (identifiers.Count == 0)
        {
            _logger.Information("No open issues found to write as context");
            return 0;
        }

        // Fetch each issue's detail and write to workspace
        var writtenCount = 0;

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var detail = await issueOps.GetIssueAsync(identifier, ct);
                var filePath = Path.Combine(outputDir, $"{identifier}.md");
                var content = FormatIssueMarkdown(detail);

                await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);
                writtenCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "Failed to fetch or write open issue {Identifier}: {Error}",
                    identifier, ex.Message);
            }
        }

        _logger.Information("Wrote {WrittenCount}/{TotalCount} open issue context files",
            writtenCount, identifiers.Count);

        return writtenCount;
    }

    private async Task<List<string>> CollectIssueIdentifiersAsync(
        IAgentIssueOperations issueOps, int maxIssues, CancellationToken ct)
    {
        var identifiers = new List<string>();
        var page = 1;
        const int pageSize = 30;

        try
        {
            while (identifiers.Count < maxIssues)
            {
                ct.ThrowIfCancellationRequested();

                var result = await issueOps.ListOpenIssuesAsync(page, pageSize, labels: null, ct);

                foreach (var issue in result.Items)
                {
                    if (identifiers.Count >= maxIssues)
                        break;

                    identifiers.Add(issue.Identifier);
                }

                if (!result.HasMore || result.Items.Count == 0)
                    break;

                page++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Failed to list open issues at page {Page}: {Error}. Proceeding with {Count} identifiers collected so far.",
                page, ex.Message, identifiers.Count);
        }

        return identifiers;
    }

    /// <summary>
    /// Formats an issue detail as a markdown file with YAML front-matter.
    /// </summary>
    internal static string FormatIssueMarkdown(IssueDetail detail)
    {
        var sb = new StringBuilder();

        // YAML front-matter
        sb.AppendLine("---");
        sb.Append("identifier: \"").Append(EscapeYamlString(detail.Identifier)).AppendLine("\"");
        sb.Append("title: \"").Append(EscapeYamlString(detail.Title)).AppendLine("\"");
        sb.Append("labels: [");
        for (var i = 0; i < detail.Labels.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append(EscapeYamlString(detail.Labels[i])).Append('"');
        }
        sb.AppendLine("]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(detail.Description);

        return sb.ToString();
    }

    private static string EscapeYamlString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
