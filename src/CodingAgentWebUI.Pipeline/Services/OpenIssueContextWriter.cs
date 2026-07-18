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

    /// <summary>
    /// Number of days to look back when fetching closed sibling issues.
    /// </summary>
    internal const int ClosedIssueLookbackDays = 30;

    public OpenIssueContextWriter(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<int> WriteOpenIssueContextAsync(
        IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        CancellationToken ct)
    {
        return WriteOpenIssueContextAsync(issueOps, workspacePath, maxIssues, includeClosedSiblings: false, ct);
    }

    /// <inheritdoc />
    public async Task<int> WriteOpenIssueContextAsync(
        IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        bool includeClosedSiblings,
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

        // Budget allocation: when including closed siblings, reserve a portion for closed issues
        int openBudget;
        int closedBudget;

        if (includeClosedSiblings)
        {
            closedBudget = Math.Min(Math.Max(1, maxIssues / 4), maxIssues - 1); // 25% for closed, capped to guarantee openBudget >= 1
            openBudget = maxIssues - closedBudget;
        }
        else
        {
            openBudget = maxIssues;
            closedBudget = 0;
        }

        // Paginate through open issues to collect identifiers up to the budget
        var openIdentifiers = await CollectIssueIdentifiersAsync(issueOps, openBudget, ct);

        // Collect closed issue identifiers if requested
        var closedIdentifiers = new List<string>();
        if (includeClosedSiblings && closedBudget > 0)
        {
            closedIdentifiers = await CollectClosedIssueIdentifiersAsync(issueOps, closedBudget, ct);
            // Remove any closed identifiers that overlap with open (shouldn't happen but be safe)
            var openSet = new HashSet<string>(openIdentifiers);
            closedIdentifiers.RemoveAll(id => openSet.Contains(id));
            // TODO: After deduplication, freed slots are not reclaimed for additional issues.
            // Consider fetching more open issues to fill the remaining budget when duplicates are removed.
        }

        if (openIdentifiers.Count == 0 && closedIdentifiers.Count == 0)
        {
            _logger.Information("No issues found to write as context");
            return 0;
        }

        // Fetch each issue's detail and write to workspace
        var writtenCount = 0;

        foreach (var identifier in openIdentifiers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var detail = await issueOps.GetIssueAsync(identifier, ct);
                var filePath = Path.Combine(outputDir, $"{identifier}.md");
                var content = FormatIssueMarkdown(detail, isClosed: false);

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

        foreach (var identifier in closedIdentifiers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var detail = await issueOps.GetIssueAsync(identifier, ct);
                var filePath = Path.Combine(outputDir, $"{identifier}.md");
                var content = FormatIssueMarkdown(detail, isClosed: true);

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
                    "Failed to fetch or write closed issue {Identifier}: {Error}",
                    identifier, ex.Message);
            }
        }

        _logger.Information("Wrote {WrittenCount} issue context files (open={OpenCount}, closed={ClosedCount})",
            writtenCount, openIdentifiers.Count, closedIdentifiers.Count);

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

    private async Task<List<string>> CollectClosedIssueIdentifiersAsync(
        IAgentIssueOperations issueOps, int maxIssues, CancellationToken ct)
    {
        var identifiers = new List<string>();
        var page = 1;
        const int pageSize = 30;
        var since = DateTime.UtcNow.AddDays(-ClosedIssueLookbackDays);

        try
        {
            while (identifiers.Count < maxIssues)
            {
                ct.ThrowIfCancellationRequested();

                var result = await issueOps.ListClosedIssuesAsync(page, pageSize, labels: null, since, ct);

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
                "Failed to list closed issues at page {Page}: {Error}. Proceeding with {Count} identifiers collected so far.",
                page, ex.Message, identifiers.Count);
        }

        return identifiers;
    }

    /// <summary>
    /// Formats an issue detail as a markdown file with YAML front-matter.
    /// When <paramref name="isClosed"/> is true, includes a <c>status: closed</c> field
    /// to distinguish closed issues from open ones.
    /// </summary>
    internal static string FormatIssueMarkdown(IssueDetail detail, bool isClosed = false)
    {
        var sb = new StringBuilder();

        // YAML front-matter
        sb.AppendLine("---");
        sb.Append("identifier: \"").Append(EscapeYamlString(detail.Identifier)).AppendLine("\"");
        sb.Append("title: \"").Append(EscapeYamlString(detail.Title)).AppendLine("\"");
        if (isClosed)
        {
            sb.AppendLine("status: closed");
        }
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
