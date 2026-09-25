using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Unified label management service that routes label operations to the correct provider
/// based on <see cref="LabelTargetKind"/>.
/// All operations are best-effort (failures are caught and logged as warnings).
/// </summary>
public sealed class LabelService : ILabelService
{
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger _logger;

    public LabelService(
        IProviderConfigStore configStore,
        IProviderFactory providerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SwapLabelAsync(
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        await SwapLabelAsync(providerConfigId, identifier, newLabel, targetKind, expectedCurrentLabel: null, ct);
    }

    /// <inheritdoc />
    public async Task SwapLabelAsync(
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        string? expectedCurrentLabel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(newLabel);
        // TODO [WARNING]: Validate providerConfigId.Value is not null/empty. The previous string parameter
        // had ArgumentNullException.ThrowIfNull(providerConfigId) which is now lost because structs
        // can't be null, but default(ProviderConfigId) with Value = null can still flow through.
        // A caller passing default(ProviderConfigId) will propagate null to GetProviderConfigByIdAsync
        // and SwapIssueLabelAsync/SwapPrLabelAsync, which may throw NullReferenceException or produce
        // incorrect store lookups rather than a clear validation error at the entry point.
        // Fix: add ArgumentException.ThrowIfNullOrEmpty(providerConfigId.Value) here and in SwapLabelStrictAsync.

        // Validate the transition if the caller provides the expected current label.
        // This is observational only — invalid transitions log a warning but do NOT block.
        // TODO: Align guard condition with AgentLabelOperations.SwapAsync which also checks
        //       !string.IsNullOrEmpty(newLabel). Current divergence is benign (IsValidTransition
        //       returns true for empty targets) but may confuse future maintainers.
        // TODO: Consider that downstream AgentLabelOperations.SwapAsync does not receive
        //       expectedCurrentLabel from SwapIssueLabelAsync/SwapPrLabelAsync callers — validation
        //       only happens here. If the entry point changes, this could mask issues.
        if (expectedCurrentLabel is not null)
        {
            LabelStateMachine.ValidateTransition(expectedCurrentLabel, newLabel, identifier);
        }

        _logger.Information(
            "Label swap: {Identifier} → {NewLabel} (target={TargetKind}, provider={ProviderConfigId})",
            identifier, newLabel, targetKind, providerConfigId.Value);

        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                    await SwapIssueLabelAsync(providerConfigId, identifier, newLabel, ct);
                    break;

                case LabelTargetKind.PullRequest:
                    await SwapPrLabelAsync(providerConfigId, identifier, newLabel, ct);
                    break;

                default:
                    _logger.Warning("Unknown LabelTargetKind {TargetKind}, skipping label swap", targetKind);
                    break;
            }

            _logger.Debug("Label swap completed: {Identifier} → {NewLabel}", identifier, newLabel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Failed to swap label to {Label} on {TargetKind} {Identifier}",
                newLabel, targetKind, identifier);
        }
    }

    /// <inheritdoc />
    public async Task SwapLabelStrictAsync(
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(newLabel);
        // TODO [WARNING]: Validate providerConfigId.Value is not null/empty. default(ProviderConfigId)
        // with Value = null can flow through since structs can't be null. A null Value propagates to
        // GetProviderConfigByIdAsync in SwapIssueLabelAsync/SwapPrLabelAsync, which may throw
        // NullReferenceException or produce incorrect store lookups rather than a clear validation
        // error at the entry point. Fix: add ArgumentException.ThrowIfNullOrEmpty(providerConfigId.Value)
        // here (same gap exists in SwapLabelAsync). (Correctness/DotNetSpecialist)


        _logger.Information(
            "Label swap (strict): {Identifier} → {NewLabel} (target={TargetKind}, provider={ProviderConfigId})",
            identifier, newLabel, targetKind, providerConfigId.Value);

        switch (targetKind)
        {
            case LabelTargetKind.Issue:
                await SwapIssueLabelAsync(providerConfigId, identifier, newLabel, ct, throwOnRemoveExhaustion: true);
                break;

            case LabelTargetKind.PullRequest:
                await SwapPrLabelAsync(providerConfigId, identifier, newLabel, ct, throwOnRemoveExhaustion: true);
                break;

            default:
                _logger.Warning("Unknown LabelTargetKind {TargetKind}, skipping label swap", targetKind);
                break;
        }

        _logger.Debug("Label swap (strict) completed: {Identifier} → {NewLabel}", identifier, newLabel);
    }

    /// <inheritdoc />
    public async Task<bool> EnsureAgentLabelsAsync(
        ProviderConfigId providerConfigId,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        // TODO [WARNING]: EnsureAgentLabelsAsync does not validate providerConfigId.Value before
        // forwarding it to GetProviderConfigByIdAsync. A default(ProviderConfigId) argument (where
        // Value is null) will silently return false (exception swallowed in the catch block) rather
        // than surfacing a clear validation error. Add ArgumentException.ThrowIfNullOrEmpty(providerConfigId.Value)
        // at entry if a null providerConfigId should be treated as a programming error here. (Correctness)


        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                    {
                        var issueConfig = await _configStore.GetProviderConfigByIdAsync(providerConfigId.Value, ProviderKind.Issue, ct);
                        if (issueConfig is null)
                        {
                            _logger.Warning(
                                "Issue provider config '{ConfigId}' not found for EnsureAgentLabelsAsync",
                                providerConfigId.Value);
                            return false;
                        }

                        await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
                        return await issueProvider.EnsureAgentLabelsAsync(ct);
                    }

                case LabelTargetKind.PullRequest:
                    {
                        var repoConfig = await _configStore.GetProviderConfigByIdAsync(providerConfigId.Value, ProviderKind.Repository, ct);
                        if (repoConfig is null)
                        {
                            _logger.Warning(
                                "Repository provider config '{ConfigId}' not found for EnsureAgentLabelsAsync (PR)",
                                providerConfigId.Value);
                            return false;
                        }

                        await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
                        return await repoProvider.EnsureAgentLabelsForPullRequestsAsync(ct);
                    }

                default:
                    _logger.Warning("Unknown LabelTargetKind {TargetKind} for EnsureAgentLabelsAsync", targetKind);
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Failed to ensure agent labels for {TargetKind} (config: {ConfigId})",
                targetKind, providerConfigId.Value);
            return false;
        }
    }

    /// <summary>
    /// Swaps labels on an issue via IIssueProvider.
    /// </summary>
    private async Task SwapIssueLabelAsync(
        ProviderConfigId issueProviderConfigId,
        IssueIdentifier issueIdentifier,
        string newLabel,
        CancellationToken ct,
        bool throwOnRemoveExhaustion = false)
    {
        var issueConfig = await _configStore.GetProviderConfigByIdAsync(issueProviderConfigId.Value, ProviderKind.Issue, ct);
        if (issueConfig is null)
        {
            _logger.Warning(
                "Issue provider config '{ConfigId}' not found, skipping label swap for issue {IssueIdentifier}",
                issueProviderConfigId.Value, issueIdentifier);
            return;
        }

        await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);

        // Fetch the issue's current labels so the remove phase only targets labels that are
        // actually present, eliminating the DELETE 404s that fire when all AgentLabels.All
        // entries are removed unconditionally. On any fetch failure, currentLabels stays null
        // and SwapAsync falls back to the full sweep (Requirement #2: a failed read must never
        // leave stale labels behind).
        IReadOnlyList<string>? currentLabels = null;
        try
        {
            var issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
            currentLabels = issueDetail.Labels;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // TODO (WARNING): An OperationCanceledException caused by an HTTP timeout (e.g.
            // TaskCanceledException wrapping a timeout) will be re-thrown here rather than
            // falling back to the full sweep, aborting the swap entirely. This matches existing
            // patterns in the codebase and is correct for true cancellation, but a network
            // timeout surfaced as OperationCanceledException will silently skip the swap.
            // Consider inspecting ex.CancellationToken or catching TaskCanceledException
            // separately if timeout-triggered fallback is desired. See issue #2971.
            _logger.Warning(ex,
                "LabelService: failed to fetch current labels for issue {IssueIdentifier} — falling back to full label sweep",
                issueIdentifier);
        }

        await AgentLabelOperations.SwapAsync(
            (label, c) => issueProvider.RemoveLabelAsync(issueIdentifier, label, c),
            (label, c) => issueProvider.AddLabelAsync(issueIdentifier, label, c),
            newLabel,
            ct,
            identifier: issueIdentifier,
            throwOnRemoveExhaustion: throwOnRemoveExhaustion,
            currentLabels: currentLabels);
    }

    /// <summary>
    /// Swaps labels on a pull request via IRepositoryProvider.
    /// </summary>
    private async Task SwapPrLabelAsync(
        ProviderConfigId repoProviderConfigId,
        IssueIdentifier prIdentifier,
        string newLabel,
        CancellationToken ct,
        bool throwOnRemoveExhaustion = false)
    {
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(repoProviderConfigId.Value, ProviderKind.Repository, ct);
        if (repoConfig is null)
        {
            _logger.Warning(
                "Repository provider config '{ConfigId}' not found, skipping label swap for PR {PrIdentifier}",
                repoProviderConfigId.Value, prIdentifier);
            return;
        }

        if (!int.TryParse(prIdentifier, out var prNumber))
        {
            _logger.Warning(
                "PR identifier '{PrIdentifier}' is not a valid integer, skipping label swap",
                prIdentifier);
            return;
        }

        await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);

        // TODO (WARNING): SwapPrLabelAsync does not fetch the PR's current labels before calling
        // SwapAsync, so PR label swaps still iterate all of AgentLabels.All unconditionally and
        // emit a DELETE 404 for every label that isn't present. The SwapIssueLabelAsync path above
        // was fixed with a GetIssueAsync prefetch; apply the same pattern here using
        // repoProvider.GetPullRequestLabelsAsync (or equivalent) to fetch current PR labels and
        // pass them as currentLabels. Tempo data (AgentHub/ReportJobCompleted spans) confirms this
        // path also contributes to the observed 404s. See issue #2971.
        await AgentLabelOperations.SwapAsync(
            (label, c) => repoProvider.RemovePrLabelAsync(prNumber, label, c),
            (label, c) => repoProvider.AddPrLabelAsync(prNumber, label, c),
            newLabel,
            ct,
            identifier: prIdentifier,
            throwOnRemoveExhaustion: throwOnRemoveExhaustion);
    }
}
