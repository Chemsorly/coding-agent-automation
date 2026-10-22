using CodingAgent.Api.Browse;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Api.Dispatch;

/// <summary>A dispatch the API refused. The message is shown to the user as it is.</summary>
public sealed class DispatchRefusedException(string message) : Exception(message);

/// <summary>
/// Manual dispatch (spec 051 2.3c): from the AgentCoding drawers, moved from the web host's drawer
/// services, and the RunPage's re-dispatch. A drawer request names only the template and the issue or
/// pull request. The API reads the item from the template's providers with the stored credentials, so
/// its labels and description come from the tracker, not the browser. Every dispatch is enqueued through
/// <see cref="IDispatchOrchestrationService"/> and the in-process distributor, and waits as Pending for
/// the Scheduler's dispatch loop. Each call returns the message the page shows. A refusal throws
/// <see cref="DispatchRefusedException"/>; a provider that fails throws <see cref="TemplateBrowseException"/>.
/// </summary>
internal sealed class ManualDispatchService(
    IProjectStore projects,
    IProviderConfigStore providerConfigs,
    IProviderFactory providerFactory,
    IDependencyChecker dependencyChecker,
    IDispatchOrchestrationService orchestration)
{
    private const string ProvidersGone = "Template references providers that no longer exist.";

    /// <summary>
    /// Dispatches an issue of the template's tracker for implementation. An issue with open dependencies,
    /// or one awaiting epic review, is refused.
    /// </summary>
    public async Task<string> DispatchIssueAsync(BrowseTemplate target, string issueIdentifier, CancellationToken ct)
    {
        var template = target.Template;
        var trackerConfig = await providerConfigs.GetProviderConfigByIdAsync(template.IssueProviderId, ProviderKind.Issue, ct);
        if (trackerConfig is null || !await ExistsAsync(template.RepoProviderId, ProviderKind.Repository, ct))
            throw new DispatchRefusedException(ProvidersGone);

        await using var tracker = providerFactory.CreateIssueProvider(trackerConfig);
        var issue = await LoadIssueAsync(tracker, issueIdentifier, ct);

        // TODO: The single-provider CheckAsync overload is used here. It forwards to the
        // cross-tracker overload with empty allProviders/providerUrlPrefixes, which means
        // any URL-based cross-tracker dependency (e.g. "Depends on https://github.com/acme/api/issues/40"
        // written by the decomposition step) will always be logged as "No configured issue
        // provider matches URL dependency" and treated as unresolved — permanently blocking
        // dispatch from the drawer even after the dependency closes. Fix: pass all configured
        // issue providers and their URL prefixes to the full cross-tracker overload so URL deps
        // are resolved correctly here too.
        var dependencies = await dependencyChecker.CheckAsync(issue.Identifier, issue.Description, tracker, new Dictionary<int, bool>(), ct);
        if (!dependencies.IsReady)
        {
            var blockedBy = dependencies.BlockedBy.Select(n => $"#{n}").Concat(dependencies.BlockedByUrls ?? []);
            throw new DispatchRefusedException($"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", blockedBy)}");
        }

        // EpicReview is a terminal state in the epic decomposition workflow. Unlike other terminal
        // labels (agent:done, agent:cancelled, etc.) which represent completed or aborted
        // implementation runs and can be force-requeued via manual dispatch, agent:epic-review marks
        // an issue that is awaiting human approval of the epic plan. Re-dispatching it as an
        // implementation job would bypass the approval gate entirely. Reject early so the UI shows
        // a clean error rather than a confusing 409 from the database unique-index constraint.
        if (issue.Labels.Any(l => StringComparer.OrdinalIgnoreCase.Equals(l, AgentLabels.EpicReview)))
            throw new DispatchRefusedException($"Cannot dispatch — issue is in state '{AgentLabels.EpicReview}' which requires human approval before it can proceed. Use the epic approval workflow instead.");

        await ClearBlockingLabelAsync(tracker, issue, ct);

        var request = await orchestration.PrepareDistributionRequestAsync(new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier = issue.Identifier,
            IssueProviderId = template.IssueProviderId,
            RepoProviderId = template.RepoProviderId,
            BrainProviderId = template.BrainProviderId,
            PipelineProviderId = template.PipelineProviderId,
            InitiatedBy = InitiatedByConstants.Manual,
            Project = ProjectOf(target)
        }, ct);
        await DistributeAsync(request, "Could not dispatch — distribution failed.", ct);
        return $"⏳ Queued #{issue.Identifier} — the job controller will start an agent pod for it";
    }

    /// <summary>Dispatches an open pull request of the template's repository for review.</summary>
    public async Task<string> DispatchPullRequestAsync(BrowseTemplate target, int number, CancellationToken ct)
    {
        var template = target.Template;
        var repositoryConfig = await providerConfigs.GetProviderConfigByIdAsync(template.RepoProviderId, ProviderKind.Repository, ct)
            ?? throw new DispatchRefusedException(ProvidersGone);

        await using var repository = providerFactory.CreateRepositoryProvider(repositoryConfig);
        var pr = await TemplateBrowseService.CallAsync(() => repository.GetOpenPullRequestAsync(number, ct), $"Failed to load PR #{number}")
            ?? throw new DispatchRefusedException($"PR #{number} is not open.");

        var request = await orchestration.PrepareReviewDistributionRequestAsync(new ReviewDispatchRequest
        {
            PrIdentifier = pr.Identifier,
            PrNumber = pr.Number,
            PrBranchName = pr.BranchName,
            PrTitle = pr.Title ?? "",
            PrUrl = pr.Url,
            PrTargetBranch = pr.TargetBranch,
            PrDescription = pr.Description,
            PrAuthor = pr.Author,
            IssueProviderId = template.IssueProviderId,
            RepoProviderId = template.RepoProviderId,
            BrainProviderId = template.BrainProviderId,
            InitiatedBy = InitiatedByConstants.Manual
        }, ProjectOf(target), ct);
        await DistributeAsync(request, $"PR #{pr.Identifier} is already being processed or queued.", ct);
        return $"⏳ Queued PR #{pr.Identifier} for review — the job controller will start an agent pod for it";
    }

    /// <summary>
    /// Dispatches an epic of the epic tracker (the project's, otherwise the template's own) for analysis,
    /// or for decomposition once it is approved. The run is bound to that tracker; the template executes it.
    /// </summary>
    public async Task<string> DispatchEpicAsync(BrowseTemplate target, string issueIdentifier, CancellationToken ct)
    {
        var template = target.Template;
        if (!await ExistsAsync(template.IssueProviderId, ProviderKind.Issue, ct) || !await ExistsAsync(template.RepoProviderId, ProviderKind.Repository, ct))
            throw new DispatchRefusedException(ProvidersGone);

        var epicTrackerId = TemplateBrowseService.EpicTrackerOf(target);
        var trackerConfig = await providerConfigs.GetProviderConfigByIdAsync(epicTrackerId, ProviderKind.Issue, ct)
            ?? throw new DispatchRefusedException("The project's epic issue provider no longer exists.");

        await using var tracker = providerFactory.CreateIssueProvider(trackerConfig);
        var epic = await LoadIssueAsync(tracker, issueIdentifier, ct);

        var phase = epic.Labels.Contains(AgentLabels.EpicApproved, StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
        var phaseLabel = phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

        var request = await orchestration.PrepareDecompositionDistributionRequestAsync(new DecompositionDispatchOrchestrationRequest
        {
            EpicIdentifier = epic.Identifier,
            EpicTitle = epic.Title ?? "",
            PhaseType = phase,
            IssueProviderId = epicTrackerId,
            RepoProviderId = template.RepoProviderId,
            BrainProviderId = template.BrainProviderId,
            InitiatedBy = InitiatedByConstants.Manual,
            Project = ProjectOf(target)
        }, ct);
        await DistributeAsync(request, "Could not dispatch — epic is already being processed or queued, or no agents are available.", ct);
        return $"⏳ Queued epic #{epic.Identifier} for {phaseLabel} — the job controller will start an agent pod for it";
    }

    /// <summary>
    /// Queues a new implementation run for the issue of a failed, cancelled or conflicted implementation
    /// run (the RunPage's re-dispatch, D5). It uses the run's providers and project. Unlike the drawers,
    /// it does not clear a blocking label first, as before. Redispatch used to create the work item
    /// directly as Dispatched through <c>POST /api/work-items/dispatch</c>; it now waits in the queue
    /// like every other dispatch.
    /// </summary>
    public async Task<string> RedispatchRunAsync(PipelineRunSummary run, CancellationToken ct)
    {
        if (!CanRedispatch(run))
            throw new DispatchRefusedException("Only a failed, cancelled or conflicted implementation run can be re-dispatched.");

        var projectId = ProjectIds.Normalize(run.ProjectId);
        var project = projectId is null
            ? null
            : (await projects.LoadProjectsAsync(ct)).FirstOrDefault(p => ProjectIds.Normalize(p.Id) == projectId);

        var request = await orchestration.PrepareDistributionRequestAsync(new ImplementationDispatchOrchestrationRequest
        {
            IssueIdentifier = run.IssueIdentifier,
            IssueProviderId = run.IssueProviderConfigId!,
            RepoProviderId = run.RepoProviderConfigId!,
            BrainProviderId = run.BrainProviderConfigId,
            PipelineProviderId = run.PipelineProviderConfigId,
            InitiatedBy = InitiatedByConstants.Manual,
            Project = OrUnnamed(project)
        }, ct);
        // Unlike the drawers, a re-dispatch of an issue that already has live work is refused: the
        // operator asked for a new run, and none was created.
        var outcome = await DistributeAsync(request, "Could not dispatch — distribution failed.", ct);
        if (outcome.AlreadyExists)
            throw new DispatchRefusedException($"Issue #{run.IssueIdentifier} already has queued or running work.");
        return $"⏳ Queued #{run.IssueIdentifier} — the job controller will start an agent pod for it";
    }

    /// <summary>Whether a run can be re-dispatched: a failed, cancelled or conflicted implementation run whose providers are known.</summary>
    internal static bool CanRedispatch(PipelineRunSummary run) =>
        run.RunType == PipelineRunType.Implementation
        && run.FinalStep is PipelineStep.Failed or PipelineStep.Cancelled or PipelineStep.ConflictRestart
        && !string.IsNullOrEmpty(run.IssueProviderConfigId)
        && !string.IsNullOrEmpty(run.RepoProviderConfigId);

    /// <summary>
    /// Manual dispatch is an explicit force-requeue: clear any blocking labels and set agent:next
    /// before creating the WorkItem. Without this, DispatchLoop would see the blocking label,
    /// cancel the WorkItem, and re-stamp the same label — a self-reinforcing cancellation loop.
    /// Only fires when the issue actually carries a blocking label (no-op for clean issues).
    /// </summary>
    /// <remarks>
    /// Note on multiple blocking labels: FirstOrDefault picks the first one as expectedCurrentLabel
    /// for LabelStateMachine logging. AgentLabelOperations.SwapAsync iterates AgentLabels.All and
    /// removes every agent label except agent:next, so ALL blocking labels are cleared regardless
    /// of which one is passed as expectedCurrentLabel.
    /// </remarks>
    private static async Task ClearBlockingLabelAsync(IIssueProvider tracker, IssueDetail issue, CancellationToken ct)
    {
        var blockingLabel = issue.Labels.FirstOrDefault(l => AgentLabels.DispatchIneligibleLabels.Contains(l));
        if (blockingLabel is null)
            return;

        try
        {
            Log.Information(
                "ManualDispatchService: manual dispatch — clearing blocking label {BlockingLabel} and setting agent:next on issue {IssueIdentifier}",
                blockingLabel, issue.Identifier);
            await AgentLabelOperations.SwapAsync(
                removeLabel: (label, c) => tracker.RemoveLabelAsync(issue.Identifier, label, c),
                addLabel: (label, c) => tracker.AddLabelAsync(issue.Identifier, label, c),
                newLabel: AgentLabels.Next,
                ct: ct,
                options: new LabelSwapOptions
                {
                    ExpectedCurrentLabel = blockingLabel,
                    Identifier = issue.Identifier
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex,
                "ManualDispatchService: failed to clear blocking label {BlockingLabel} on issue {IssueIdentifier} before dispatch",
                blockingLabel, issue.Identifier);
            throw new DispatchRefusedException($"Could not clear blocking label '{blockingLabel}' before dispatch — {ex.Message}");
        }
    }

    private async Task<DispatchOutcome> DistributeAsync(JobDistributionRequest? request, string distributionFailed, CancellationToken ct)
    {
        if (request is null)
            throw new DispatchRefusedException("Could not dispatch — orchestration preparation failed (check logs for details).");

        // The in-process distributor only enqueues, so a successful dispatch is always "queued". A refusal carries
        // its reason, such as "No agent route: set the default route in Settings → Agents → Routing" (spec 053 req 2.9).
        var outcome = await orchestration.DistributeAndFinalizeAsync(request, ct);
        if (!outcome.Success)
            throw new DispatchRefusedException(outcome is { IsRefused: true, ErrorMessage: { } reason } ? reason : distributionFailed);
        return outcome;
    }

    /// <summary>The orchestration needs a project; a template of no project dispatches under an unnamed one.</summary>
    private static PipelineProject ProjectOf(BrowseTemplate target) => OrUnnamed(target.Project);

    private static PipelineProject OrUnnamed(PipelineProject? project) => project ?? new PipelineProject { Id = "", Name = "Unknown" };

    private async Task<bool> ExistsAsync(string providerId, ProviderKind kind, CancellationToken ct) =>
        await providerConfigs.GetProviderConfigByIdAsync(providerId, kind, ct) is not null;

    private static Task<IssueDetail> LoadIssueAsync(IIssueProvider tracker, string identifier, CancellationToken ct) =>
        TemplateBrowseService.CallAsync(() => tracker.GetIssueAsync(identifier, ct), $"Failed to load issue #{identifier}");
}
