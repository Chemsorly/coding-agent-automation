using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Computes staleness counters that drive the automatic consolidation triggers:
/// how many runs, feedback entries, or closed issues have accumulated since the last successful
/// consolidation run of a given scope.
/// </summary>
public class ConsolidationStalenessService
{
    private readonly IPipelineRunHistoryService _history;
    private readonly IProviderConfigStore _providerConfigs;
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ConsolidationStalenessService(
        IPipelineRunHistoryService history,
        IProviderConfigStore providerConfigs,
        IProviderFactory providerFactory,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(providerConfigs);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _history = history;
        _providerConfigs = providerConfigs;
        _providerFactory = providerFactory;
        _logger = logger.ForContext<ConsolidationStalenessService>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Protected constructor for Moq / Castle DynamicProxy subclassing in tests.
    /// All methods are virtual so a subclass proxy can intercept calls.
    /// </summary>
    protected ConsolidationStalenessService()
    {
        _history = null!;
        _providerConfigs = null!;
        _providerFactory = null!;
        _logger = null!;
        _timeProvider = TimeProvider.System;
    }

    /// <summary>
    /// Returns the number of non-consolidation runs that pushed an inbox file to
    /// <paramref name="brainProviderId"/> since the last successful brain consolidation for that brain.
    /// </summary>
    public virtual async Task<int> CountBrainRunsSinceLastConsolidationAsync(
        string brainProviderId, CancellationToken ct)
    {
        // NOTE [WARNING]: no ArgumentNullException.ThrowIfNull(brainProviderId) guard.
        // Passing null silently produces scope key "BrainConsolidation:global" and matches
        // runs where BrainProviderConfigId is null, returning an incorrect count.
        var scopeKey = new IssueIdentifier(
            ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, brainProviderId, null));

        var since = await ConsolidationRunHistoryContext.GetLastSuccessfulRunAsync(_history, scopeKey, ct);

        // Loop pages (up to 20 × 500) to count all runs with an inbox file since the last review.
        // A single-page cap missed brain runs when inbox volume exceeded 500 since the last review.
        int count = 0;
        const int pageSize = 500;
        const int maxPages = 20;
        for (var page = 1; page <= maxPages; page++)
        {
            var result = await _history.GetRunHistoryAsync(
                new RunHistoryQuery(Page: page, PageSize: pageSize, Since: since),
                ct);

            count += result.Items.Count(r =>
                r.RunType != PipelineRunType.Consolidation &&
                !string.IsNullOrEmpty(r.BrainInboxFile) &&
                r.BrainProviderConfigId == brainProviderId);

            if (!result.HasMore)
                break;
        }

        return count;
    }

    /// <summary>
    /// Returns the number of issues closed in the template's tracker (by the last-update date filter)
    /// since the last successful refactoring scan for that template, excluding issues the scan itself created.
    /// </summary>
    public virtual async Task<int> CountClosedIssuesSinceLastScanAsync(
        PipelineJobTemplate template, CancellationToken ct)
    {
        // NOTE [WARNING]: no ArgumentNullException.ThrowIfNull(template) guard.
        // Passing null throws NullReferenceException on template.IssueProviderId rather
        // than a clear parameter validation error.
        var issueConfig = await _providerConfigs.GetProviderConfigByIdAsync(
            template.IssueProviderId, ProviderKind.Issue, ct);

        if (issueConfig is null)
        {
            _logger.Warning(
                "ConsolidationStalenessService: issue provider config '{ConfigId}' not found for template '{TemplateId}' — cannot count closed issues",
                template.IssueProviderId, template.Id);
            return 0;
        }

        await using var provider = _providerFactory.CreateIssueProvider(issueConfig);
        await provider.ValidateAsync(ct);

        var scopeKey = new IssueIdentifier(
            ConsolidationService.ScopeKey(ConsolidationRunType.RefactoringDetection, null, template.Id));

        var since = await ConsolidationRunHistoryContext.GetLastSuccessfulRunAsync(_history, scopeKey, ct);

        // Fall back to 30 days before now when there has been no previous scan.
        var sinceDateTime = since.HasValue
            ? since.Value.UtcDateTime
            : _timeProvider.GetUtcNow().AddDays(-30).UtcDateTime;

        var result = await provider.ListClosedIssuesAsync(1, 100, null, sinceDateTime, ct);

        return result.Items.Count(issue => !RefactoringScanIssues.IsScanIssue(issue));
    }

    /// <summary>
    /// Returns the number of open issues in the template's tracker that the refactoring scan created.
    /// Used for backpressure: skips automatic scan when the open proposal count already meets the limit.
    /// </summary>
    public virtual async Task<int> CountOpenScanIssuesAsync(PipelineJobTemplate template, CancellationToken ct)
    {
        // NOTE [WARNING]: no ArgumentNullException.ThrowIfNull(template) guard.
        // Passing null throws NullReferenceException on template.IssueProviderId rather
        // than a clear parameter validation error.
        var issueConfig = await _providerConfigs.GetProviderConfigByIdAsync(
            template.IssueProviderId, ProviderKind.Issue, ct);

        if (issueConfig is null)
        {
            _logger.Warning(
                "ConsolidationStalenessService: issue provider config '{ConfigId}' not found for template '{TemplateId}' — cannot count open scan issues",
                template.IssueProviderId, template.Id);
            return 0;
        }

        await using var provider = _providerFactory.CreateIssueProvider(issueConfig);
        await provider.ValidateAsync(ct);

        var result = await provider.ListOpenIssuesAsync(1, 100, null, ct);

        return result.Items.Count(issue => RefactoringScanIssues.IsScanIssue(issue));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the latest consolidation run for <paramref name="scopeKey"/>
    /// ended in failure, indicating the scope is paused until a successful run clears it.
    /// Returns <see langword="false"/> when there are no runs or the latest run succeeded.
    /// </summary>
    public virtual async Task<bool> IsPausedAsync(string scopeKey, CancellationToken ct)
    {
        // NOTE [WARNING]: no ArgumentNullException.ThrowIfNull(scopeKey) guard.
        // Passing null causes the LINQ Where to silently match nothing, returning false
        // (not paused) instead of surfacing the bad input as a parameter validation error.
        var result = await _history.GetRunHistoryAsync(
            new RunHistoryQuery(Page: 1, PageSize: 100, RunType: PipelineRunType.Consolidation),
            ct);

        var latestRun = result.Items
            .Where(r => r.IssueIdentifier == scopeKey)
            .MaxBy(r => r.StartedAtOffset);

        return latestRun?.FinalStep == PipelineStep.Failed;
    }
}
