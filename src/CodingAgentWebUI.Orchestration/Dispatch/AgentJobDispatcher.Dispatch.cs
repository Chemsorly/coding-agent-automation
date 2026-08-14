using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class AgentJobDispatcher
{
    /// <inheritdoc />
    public async Task<bool> TryDispatchAsync(
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? issueTitle = null,
        PipelineProject? project = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentNullException.ThrowIfNull(initiatedBy);
        ArgumentException.ThrowIfNullOrEmpty(issueProviderId.Value, nameof(issueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(repoProviderId.Value, nameof(repoProviderId));

        // Check if already being processed
        if (_orchestration.IsIssueBeingProcessed(issueIdentifier, issueProviderId) || _dispatcher.IsIssueQueued(issueIdentifier, issueProviderId))
        {
            _logger.Information("Issue {IssueIdentifier} already being processed or queued, skipping dispatch", issueIdentifier);
            return false;
        }

        return await TryDispatchCoreAsync(
            issueIdentifier,
            repoProviderId,
            (agent, requiredLabels, token) => DispatchToAgentAsync(
                agent, issueIdentifier, issueProviderId, repoProviderId,
                brainProviderId, pipelineProviderId, initiatedBy, requiredLabels, token,
                project: project),
            requiredLabels => new PendingJob
            {
                IssueIdentifier = issueIdentifier,
                IssueTitle = issueTitle,
                IssueProviderId = issueProviderId,
                RepoProviderId = repoProviderId,
                BrainProviderId = brainProviderId,
                PipelineProviderId = pipelineProviderId,
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = initiatedBy,
                RequiredLabels = requiredLabels,
                Project = project
            },
            "Issue {Identifier} enqueued for dispatch (no idle agents)",
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchReviewAsync(ReviewDispatchRequest request, CancellationToken ct, PipelineProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        // TODO: [WARNING] nameof(request.IssueProviderId) and nameof(request.RepoProviderId) evaluate to
        // the property names "IssueProviderId"/"RepoProviderId", not the method parameter name "request".
        // This is non-idiomatic for ArgumentException.ParamName; .NET convention uses the method parameter
        // name (i.e., nameof(request)). Fixing this would also require updating the corresponding test
        // assertions in AgentJobDispatcherTests.cs (TryDispatchReviewAsync_Default*ProviderId tests).
        ArgumentException.ThrowIfNullOrEmpty(request.IssueProviderId.Value, nameof(request.IssueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(request.RepoProviderId.Value, nameof(request.RepoProviderId));

        // Check if already being processed
        if (IsIssueBeingProcessedOrQueued(request.PrIdentifier, request.IssueProviderId))
        {
            _logger.Information("PR {PrIdentifier} already being processed or queued, skipping review dispatch", request.PrIdentifier);
            return false;
        }

        return await TryDispatchCoreAsync(
            request.PrIdentifier,
            request.RepoProviderId,
            (agent, requiredLabels, token) => DispatchReviewToAgentAsync(
                agent, request, requiredLabels, token, project: project),
            requiredLabels => new PendingJob
            {
                IssueIdentifier = request.PrIdentifier,
                IssueTitle = request.PrTitle,
                IssueProviderId = request.IssueProviderId,
                RepoProviderId = request.RepoProviderId,
                BrainProviderId = request.BrainProviderId,
                PipelineProviderId = null,
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = request.InitiatedBy,
                RequiredLabels = requiredLabels,
                RunType = PipelineRunType.Review,
                PrBranchName = request.PrBranchName,
                PrDescription = request.PrDescription,
                PrAuthor = request.PrAuthor,
                PrUrl = request.PrUrl,
                PrTargetBranch = request.PrTargetBranch,
                Project = project
            },
            "PR review {Identifier} enqueued for dispatch (no idle agents)",
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchDecompositionAsync(
        IssueIdentifier epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        CancellationToken ct,
        string? decompositionSource = null,
        PipelineProject? project = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(epicIdentifier.Value, nameof(epicIdentifier));
        ArgumentNullException.ThrowIfNull(epicTitle);
        ArgumentNullException.ThrowIfNull(initiatedBy);
        ArgumentException.ThrowIfNullOrEmpty(issueProviderId.Value, nameof(issueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(repoProviderId.Value, nameof(repoProviderId));

        if (phaseType is not (PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition))
            throw new ArgumentOutOfRangeException(nameof(phaseType), phaseType, "Must be DecompositionAnalysis or Decomposition");

        // Check if already being processed
        if (_orchestration.IsIssueBeingProcessed(epicIdentifier, issueProviderId) || _dispatcher.IsIssueQueued(epicIdentifier, issueProviderId))
        {
            _logger.Information("Epic {EpicIdentifier} already being processed or queued, skipping decomposition dispatch", epicIdentifier);
            return false;
        }

        return await TryDispatchCoreAsync(
            epicIdentifier,
            repoProviderId,
            (agent, requiredLabels, token) => DispatchDecompositionToAgentAsync(
                agent, epicIdentifier, epicTitle, phaseType,
                issueProviderId, repoProviderId, brainProviderId,
                initiatedBy, requiredLabels, token,
                decompositionSource: decompositionSource,
                project: project),
            requiredLabels => new PendingJob
            {
                IssueIdentifier = epicIdentifier,
                IssueTitle = epicTitle,
                IssueProviderId = issueProviderId,
                RepoProviderId = repoProviderId,
                BrainProviderId = brainProviderId,
                PipelineProviderId = null,
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = initiatedBy,
                RequiredLabels = requiredLabels,
                RunType = phaseType,
                DecompositionSource = decompositionSource,
                Project = project
            },
            "Decomposition for {Identifier} enqueued for dispatch (no idle agents)",
            ct);
    }

    private async Task<bool> TryDispatchCoreAsync(
        string identifier,
        ProviderConfigId repoProviderId,
        Func<AgentEntry, IReadOnlyList<string>, CancellationToken, Task<bool>> dispatchToAgent,
        Func<IReadOnlyList<string>, PendingJob> buildPendingJob,
        string logMessageTemplate,
        CancellationToken ct)
    {
        // Prevent dispatch during shutdown — avoids creating runs that immediately get cancelled
        if (_shutdownSignal.IsShuttingDown)
        {
            _logger.Information("Dispatch suppressed for {Identifier} — shutdown in progress", identifier);
            return false;
        }

        var config = await _infra.Resolution.ConfigStore.LoadPipelineConfigAsync(ct);
        var repoConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        var requiredLabels = JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, config);

        var agent = _dispatcher.SelectAgent(requiredLabels);

        if (agent != null)
        {
            return await dispatchToAgent(agent, requiredLabels, ct);
        }

        var enqueued = _dispatcher.EnqueueJob(buildPendingJob(requiredLabels));

        if (enqueued)
            _logger.Information(logMessageTemplate, identifier);

        return enqueued;
    }

    /// <inheritdoc />
    public async Task<bool> DispatchToAgentDirectAsync(
        AgentEntry agent,
        PendingJob job,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(requiredLabels);
        // TODO: [WARNING] nameof(job.IssueProviderId) and nameof(job.RepoProviderId) evaluate to the
        // property names "IssueProviderId"/"RepoProviderId", not the method parameter name "job".
        // This is non-idiomatic for ArgumentException.ParamName; .NET convention uses the method parameter
        // name (i.e., nameof(job)). Fixing this would also require updating the corresponding test
        // assertions in AgentJobDispatcherTests.cs (DispatchToAgentDirectAsync_Default*ProviderId tests).
        ArgumentException.ThrowIfNullOrEmpty(job.IssueProviderId.Value, nameof(job.IssueProviderId));
        ArgumentException.ThrowIfNullOrEmpty(job.RepoProviderId.Value, nameof(job.RepoProviderId));

        return job.RunType switch
        {
            PipelineRunType.Review => await DispatchReviewToAgentAsync(
                agent,
                new ReviewDispatchRequest
                {
                    PrIdentifier = job.IssueIdentifier,
                    PrBranchName = job.PrBranchName!,
                    PrTitle = job.IssueTitle ?? $"PR #{job.IssueIdentifier}",
                    PrDescription = job.PrDescription ?? string.Empty,
                    PrAuthor = job.PrAuthor,
                    PrUrl = job.PrUrl ?? string.Empty,
                    PrTargetBranch = job.PrTargetBranch ?? "main",
                    IssueProviderId = job.IssueProviderId,
                    RepoProviderId = job.RepoProviderId,
                    BrainProviderId = job.BrainProviderId,
                    InitiatedBy = job.InitiatedBy
                },
                requiredLabels,
                ct,
                project: job.Project),

            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition =>
                await DispatchDecompositionToAgentAsync(
                    agent,
                    job.IssueIdentifier,
                    job.IssueTitle ?? $"Epic #{job.IssueIdentifier}",
                    job.RunType,
                    job.IssueProviderId,
                    job.RepoProviderId,
                    job.BrainProviderId,
                    job.InitiatedBy,
                    requiredLabels,
                    ct,
                    decompositionSource: job.DecompositionSource,
                    project: job.Project),

            _ => await DispatchToAgentAsync(
                agent,
                job.IssueIdentifier,
                job.IssueProviderId,
                job.RepoProviderId,
                job.BrainProviderId,
                job.PipelineProviderId,
                job.InitiatedBy,
                requiredLabels,
                ct,
                project: job.Project)
        };
    }
}
