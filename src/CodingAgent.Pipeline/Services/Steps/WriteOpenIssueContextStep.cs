using System.Text;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Downloads open issues and writes them as markdown context files to the workspace
/// for agent deduplication. Accesses <see cref="CodingAgent.Pipeline.Interfaces.IAgentIssueOperations"/>
/// directly via <c>context.IssueOps</c> — no constructor injection required.
/// For decomposition runs, also includes recently-closed sibling issues.
/// </summary>
public sealed class WriteOpenIssueContextStep : IPipelineStep
{
    public string StepName => "WriteOpenIssueContext";

    /// <summary>
    /// Number of days to look back when fetching closed sibling issues.
    /// </summary>
    internal const int ClosedIssueLookbackDays = 30;

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.TransitionTo(PipelineStep.DownloadingOpenIssues);

        var maxIssues = context.Config.MaxOpenIssuesForContext;
        var includeClosedSiblings = IsEpicScopedRun(context.Run.RunType);

        var count = await WriteOpenIssueContextAsync(
            context.IssueOps, context.Run.WorkspacePath!, maxIssues, includeClosedSiblings,
            context.Logger, ct);

        context.Run.OpenIssuesDownloaded = count;
        context.Logger.Information("Wrote {Count} issue context files (includeClosedSiblings={IncludeClosed})",
            count, includeClosedSiblings);
        return StepResult.Continue;
    }

    /// <summary>
    /// Determines whether the run is epic-scoped (decomposition phase 1 or 2).
    /// Epic-scoped runs benefit from seeing recently-closed sibling issues.
    /// </summary>
    internal static bool IsEpicScopedRun(PipelineRunType runType) =>
        runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition;

    private static async Task<int> WriteOpenIssueContextAsync(
        Interfaces.IAgentIssueOperations issueOps,
        string workspacePath,
        int maxIssues,
        bool includeClosedSiblings,
        ILogger logger,
        CancellationToken ct)
    {
        // TODO: The null guard on issueOps is inside this private helper rather than on the public
        // ExecuteAsync method. The public method has no explicit guard on context.IssueOps, unlike
        // the old constructor which had ArgumentNullException.ThrowIfNull(writer). Consider adding
        // an explicit guard in ExecuteAsync (e.g., ArgumentNullException.ThrowIfNull(context.IssueOps))
        // to produce a clear exception at the public contract boundary.
        ArgumentNullException.ThrowIfNull(issueOps);
        ArgumentNullException.ThrowIfNull(workspacePath);

        if (maxIssues < 1)
        {
            logger.Warning("MaxIssues must be at least 1, received {MaxIssues}. Using 1.", maxIssues);
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
        var openIdentifiers = await CollectIssueIdentifiersAsync(issueOps, openBudget, logger, ct);

        // Collect closed issue identifiers if requested
        var closedIdentifiers = new List<string>();
        if (includeClosedSiblings && closedBudget > 0)
        {
            closedIdentifiers = await CollectClosedIssueIdentifiersAsync(issueOps, closedBudget, logger, ct);
            // Remove any closed identifiers that overlap with open (shouldn't happen but be safe)
            var openSet = new HashSet<string>(openIdentifiers);
            closedIdentifiers.RemoveAll(id => openSet.Contains(id));
            // TODO: After deduplication, freed slots are not reclaimed for additional issues.
            // Consider fetching more open issues to fill the remaining budget when duplicates are removed.
        }

        if (openIdentifiers.Count == 0 && closedIdentifiers.Count == 0)
        {
            logger.Information("No issues found to write as context");
            return 0;
        }

        // Fetch each issue's detail and write to workspace
        var writtenCount = 0;
        writtenCount += await WriteIssueFilesAsync(issueOps, openIdentifiers, outputDir, isClosed: false, logger, ct);
        writtenCount += await WriteIssueFilesAsync(issueOps, closedIdentifiers, outputDir, isClosed: true, logger, ct);

        var totalIdentifiers = openIdentifiers.Count + closedIdentifiers.Count;
        if (writtenCount == 0 && totalIdentifiers > 0)
        {
            logger.Warning(
                "WriteOpenIssueContext wrote 0 files despite {TotalIdentifiers} identifiers collected " +
                "(open={OpenCount}, closed={ClosedCount}) — all RequestGetIssue calls may have failed",
                totalIdentifiers, openIdentifiers.Count, closedIdentifiers.Count);
        }
        else
        {
            logger.Information("Wrote {WrittenCount} issue context files (open={OpenCount}, closed={ClosedCount})",
                writtenCount, openIdentifiers.Count, closedIdentifiers.Count);
        }

        return writtenCount;
    }

    private static async Task<int> WriteIssueFilesAsync(
        Interfaces.IAgentIssueOperations issueOps,
        List<string> identifiers,
        string outputDir,
        bool isClosed,
        ILogger logger,
        CancellationToken ct)
    {
        var writtenCount = 0;

        foreach (var identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var detail = await issueOps.GetIssueAsync(identifier, ct);
                // TODO: The identifier value comes from the remote API (ListOpenIssuesAsync /
                // ListClosedIssuesAsync) and is used unsanitised in Path.Combine. An identifier
                // containing path-traversal characters (e.g. "../../../etc/cron.d/evil") would
                // resolve outside outputDir. Mitigate by using Path.GetFileName(identifier), or
                // by validating that the resolved path starts with outputDir before writing.
                var filePath = Path.Combine(outputDir, $"{identifier}.md");
                var content = FormatIssueMarkdown(detail, isClosed);

                await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);
                writtenCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Warning(ex,
                    "Failed to fetch or write {State} issue {Identifier}: {Error}",
                    isClosed ? "closed" : "open", identifier, ex.Message);
            }
        }

        return writtenCount;
    }

    private static async Task<List<string>> CollectIssueIdentifiersAsync(
        Interfaces.IAgentIssueOperations issueOps, int maxIssues, ILogger logger, CancellationToken ct)
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
            logger.Warning(ex,
                "Failed to list open issues at page {Page}: {Error}. Proceeding with {Count} identifiers collected so far.",
                page, ex.Message, identifiers.Count);
        }

        return identifiers;
    }

    private static async Task<List<string>> CollectClosedIssueIdentifiersAsync(
        Interfaces.IAgentIssueOperations issueOps, int maxIssues, ILogger logger, CancellationToken ct)
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
            logger.Warning(ex,
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
