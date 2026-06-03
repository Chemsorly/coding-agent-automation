using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Phase 2, Step 4: Parses sub-issue JSON files from the workspace, resolves dependencies,
/// sanitizes content, and creates issues sequentially via the orchestrator proxy.
/// Enforces the configured sub-issue cap, retries transient errors, and tracks results
/// for the summary step.
///
/// Supports cross-repo decomposition routing: when a <c>ProjectContext</c> is present and
/// a proposal specifies a <c>TargetRepository</c>, the issue is routed to that template's
/// issue provider instead of the dispatching template's default provider.
/// </summary>
internal sealed class CreateSubIssuesStep : IPipelineStep
{
    /// <summary>Maximum retry attempts for transient errors.</summary>
    private const int MaxRetryAttempts = 3;

    /// <summary>Timeout for the entire issue creation phase.</summary>
    private static readonly TimeSpan CreationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Exponential backoff delays: 0s, 1s, 3s.</summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreateSubIssues");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        // 1. Transition to CreatingIssues
        context.Callbacks.TransitionTo(PipelineStep.CreatingIssues);

        // 2. Parse sub-issue files from workspace
        var workspacePath = context.Run.WorkspacePath!;
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(workspacePath, context.Logger, ct);

        if (proposals.Count == 0)
        {
            context.Logger.Warning("No valid sub-issue proposals found in workspace");
            context.Run.SubIssueResults = [];
            return StepResult.Continue;
        }

        // 3. Enforce MaxDecompositionSubIssues cap (take first N alphabetically — already sorted by parser)
        var cap = context.Config.MaxDecompositionSubIssues;
        var cappedProposals = proposals.Count > cap
            ? proposals.Take(cap).ToList()
            : proposals;

        if (proposals.Count > cap)
        {
            context.Logger.Information(
                "Sub-issue cap enforced: {Total} proposals found, processing first {Cap} alphabetically",
                proposals.Count, cap);
        }

        // 4. Create linked CancellationTokenSource with 5-minute timeout
        using var timeoutCts = new CancellationTokenSource(CreationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var creationCt = linkedCts.Token;

        // 5-10. Create issues sequentially with dependency resolution
        var resolver = new DependencyResolver();
        var results = new List<SubIssueCreationResult>();

        foreach (var proposal in cappedProposals)
        {
            // 11. On timeout: mark remaining as failed
            if (timeoutCts.IsCancellationRequested)
            {
                context.Logger.Warning(
                    "Creation timeout exceeded; marking remaining sub-issues as failed. Title: {Title}",
                    proposal.Title);

                PipelineTelemetry.SubIssuesFailed.Add(1,
                    PipelineTelemetry.BuildTags(context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName));

                results.Add(new SubIssueCreationResult
                {
                    Title = proposal.Title,
                    Success = false,
                    FailureReason = "creation timeout exceeded"
                });
                continue;
            }

            var result = await CreateSingleIssueAsync(proposal, resolver, context, creationCt);
            results.Add(result);

            // Register successful creations for dependency resolution
            if (result.Success && result.Identifier is not null)
            {
                resolver.Register(proposal.Title, result.Identifier);
            }
        }

        // 12. Store results on context for summary step
        context.Run.SubIssueResults = results;
        context.Run.DecompositionSubIssuesAttempted = results.Count;
        context.Run.DecompositionSubIssuesCreated = results.Count(r => r.Success);

        context.Logger.Information(
            "Sub-issue creation complete: {Created}/{Attempted} succeeded",
            context.Run.DecompositionSubIssuesCreated,
            context.Run.DecompositionSubIssuesAttempted);

        return StepResult.Continue;
    }

    private static async Task<SubIssueCreationResult> CreateSingleIssueAsync(
        SubIssueProposal proposal,
        DependencyResolver resolver,
        PipelineStepContext context,
        CancellationToken ct)
    {
        // 6. Sanitize title and body
        var sanitizedTitle = TextSanitizer.SanitizeTitle(proposal.Title);
        var sanitizedBody = TextSanitizer.SanitizeMarkdown(proposal.Body);

        // 7. Resolve dependencies and prepend to body
        var dependencyLines = resolver.Resolve(proposal.Dependencies, context.Logger);
        if (dependencyLines.Count > 0)
        {
            var depSection = string.Join("\n", dependencyLines);
            sanitizedBody = $"{depSection}\n\n{sanitizedBody}";
        }

        // 8. Apply labels: agent:next + agent:generated + custom labels from proposal
        var labels = new List<string> { AgentLabels.Next, AgentLabels.Generated };
        foreach (var label in proposal.Labels)
        {
            if (!string.IsNullOrWhiteSpace(label) &&
                !labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(label);
            }
        }

        // Resolve target issue provider for cross-repo routing
        var targetProviderId = ResolveTargetIssueProviderId(proposal, context);

        // 9. Retry transient errors (3 attempts, exponential backoff: 0s, 1s, 3s)
        for (var attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = RetryDelays[attempt];
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct);
                    }
                }

                // Create the issue — route to specific provider if target is resolved,
                // otherwise use the dispatching template's default provider.
                var created = targetProviderId is not null
                    ? await context.IssueOps.CreateIssueForProviderAsync(
                        targetProviderId, sanitizedTitle, sanitizedBody, labels, ct)
                    : await context.IssueOps.CreateIssueAsync(
                        sanitizedTitle, sanitizedBody, labels, ct);

                context.Logger.Information(
                    "Created issue {Identifier}: {Title}{RouteInfo}",
                    created.Identifier, sanitizedTitle,
                    targetProviderId is not null ? $" (routed to provider {targetProviderId})" : "");

                PipelineTelemetry.SubIssuesCreated.Add(1,
                    PipelineTelemetry.BuildTags(context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName));

                return new SubIssueCreationResult
                {
                    Title = proposal.Title,
                    Success = true,
                    Identifier = created.Identifier,
                    Url = created.Url
                };
            }
            catch (OperationCanceledException)
            {
                // Timeout or external cancellation — don't retry
                PipelineTelemetry.SubIssuesFailed.Add(1,
                    PipelineTelemetry.BuildTags(context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName));
                return new SubIssueCreationResult
                {
                    Title = proposal.Title,
                    Success = false,
                    FailureReason = "creation timeout exceeded"
                };
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                context.Logger.Warning(
                    ex,
                    "Transient error creating issue '{Title}' (attempt {Attempt}/{MaxAttempts})",
                    proposal.Title, attempt + 1, MaxRetryAttempts);

                // If this was the last attempt, fall through to failure
                if (attempt == MaxRetryAttempts - 1)
                {
                    context.Logger.Warning(
                        "Exhausted retries for issue '{Title}': {Error}",
                        proposal.Title, ex.Message);

                    PipelineTelemetry.SubIssuesFailed.Add(1,
                        PipelineTelemetry.BuildTags(context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName));

                    return new SubIssueCreationResult
                    {
                        Title = proposal.Title,
                        Success = false,
                        FailureReason = $"Transient error after {MaxRetryAttempts} attempts: {ex.Message}"
                    };
                }
            }
            catch (Exception ex)
            {
                // Non-transient error — skip immediately without retry
                context.Logger.Warning(
                    ex,
                    "Non-transient error creating issue '{Title}': {Error}",
                    proposal.Title, ex.Message);

                PipelineTelemetry.SubIssuesFailed.Add(1,
                    PipelineTelemetry.BuildTags(context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName));

                return new SubIssueCreationResult
                {
                    Title = proposal.Title,
                    Success = false,
                    FailureReason = $"Non-transient error: {ex.Message}"
                };
            }
        }

        // Should not reach here, but safety net
        return new SubIssueCreationResult
        {
            Title = proposal.Title,
            Success = false,
            FailureReason = "Unexpected: exhausted retry loop without result"
        };
    }

    /// <summary>
    /// Resolves the target issue provider config ID for a decomposed issue proposal.
    /// When <c>targetRepository</c> matches a template name in the project context,
    /// returns that template's <c>IssueProviderId</c>.
    /// Falls back to null (use dispatching template's default provider) when:
    /// - <c>targetRepository</c> is null or empty (default behavior)
    /// - No <c>ProjectContext</c> is available (per-template decomposition, backward compatible)
    /// - <c>targetRepository</c> does not match any template name (logs warning)
    /// </summary>
    private static string? ResolveTargetIssueProviderId(SubIssueProposal proposal, PipelineStepContext context)
        => ResolveTargetIssueProviderId(proposal.TargetRepository, context.ProjectContext, context.Logger);

    /// <summary>
    /// Pure routing logic extracted for testability. Resolves a <c>targetRepository</c> value
    /// to an issue provider config ID using the project context's repository list.
    /// Returns null when the target is unresolvable (fallback to dispatching template's default provider).
    /// Never throws for invalid inputs — always falls back safely.
    /// </summary>
    internal static string? ResolveTargetIssueProviderId(
        string? targetRepository,
        DecompositionProjectContext? projectContext,
        Serilog.ILogger? logger = null)
    {
        // No target specified — use dispatching template's default provider
        if (string.IsNullOrEmpty(targetRepository))
            return null;

        // No project context — per-template decomposition, use default provider
        if (projectContext is null)
        {
            logger?.Warning(
                "targetRepository '{Target}' specified but no project context is available; using default provider",
                targetRepository);
            return null;
        }

        // Resolve targetRepository → matching repository target → issue provider ID
        var targetRepo = projectContext.Repositories
            .FirstOrDefault(r => string.Equals(r.TemplateName, targetRepository, StringComparison.Ordinal));

        if (targetRepo is null)
        {
            logger?.Warning(
                "Target repository '{Target}' not found in project '{Project}'; using dispatching template's provider",
                targetRepository, projectContext.ProjectName);
            return null;
        }

        // Target found but no issue provider ID configured
        if (string.IsNullOrEmpty(targetRepo.IssueProviderId))
        {
            logger?.Warning(
                "Target repository '{Target}' in project '{Project}' has no IssueProviderId; using dispatching template's provider",
                targetRepository, projectContext.ProjectName);
            return null;
        }

        return targetRepo.IssueProviderId;
    }

    /// <summary>
    /// Determines whether an exception represents a transient error that should be retried.
    /// Transient errors include network failures, rate limits, and server errors.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        return ex is HttpRequestException httpEx &&
               (httpEx.StatusCode is null || // Network-level failure (no response)
                (int)httpEx.StatusCode.Value >= 500 || // Server errors
                httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests || // Rate limit
                httpEx.StatusCode == System.Net.HttpStatusCode.RequestTimeout);
    }
}
