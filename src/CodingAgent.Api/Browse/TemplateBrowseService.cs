using CodingAgent.Api.Dispatch;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Api.Browse;

/// <summary>A template and the project that owns it (null for a template of no project).</summary>
internal sealed record BrowseTemplate(PipelineJobTemplate Template, PipelineProject? Project);

/// <summary>
/// A browse that could not be answered: the template's provider is gone or failed. The message is
/// shown to the user as it is.
/// </summary>
public sealed class TemplateBrowseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the dispatch drawers browse for a template (spec 051 2.3b, moved from the web host's drawer
/// services): its tracker's open issues, their dependency readiness and labels, its repository's open
/// pull requests, the epics of its project's epic tracker, and which of them already have work. The
/// providers are called with the stored credentials.
/// </summary>
internal sealed class TemplateBrowseService(
    IProjectStore projects,
    IProviderConfigStore providerConfigs,
    IProviderFactory providerFactory,
    IDependencyChecker dependencyChecker,
    WorkItemStore workItems)
{
    /// <summary>Issues and pull requests per drawer page.</summary>
    internal const int PageSize = 15;

    /// <summary>Epics per page and label: the drawer shows the new and the approved ones together.</summary>
    internal const int EpicPageSize = 8;

    public async Task<BrowseTemplate?> FindAsync(string templateId, CancellationToken ct)
    {
        var template = (await projects.LoadAllTemplatesAsync(ct)).FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return null;
        var owner = (await projects.LoadProjectsAsync(ct)).FirstOrDefault(p => p.TemplateIds.Contains(templateId));
        return new BrowseTemplate(template, owner);
    }

    public async Task<PagedResult<IssueSummary>> ListIssuesAsync(BrowseTemplate browse, int page, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        await using var provider = await IssueProviderAsync(browse.Template.IssueProviderId, "Issue provider not found for this template.", ct);
        return await CallAsync(() => provider.ListOpenIssuesAsync(page, PageSize, NullIfEmpty(labels), ct), "Failed to load issues");
    }

    /// <summary>
    /// The dependency readiness of the issues on a page, by identifier. Best-effort: when a check
    /// fails, the issues checked so far are still returned.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DependencyCheckResult>> CheckReadinessAsync(
        BrowseTemplate browse, int page, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        var readiness = new Dictionary<string, DependencyCheckResult>(StringComparer.Ordinal);
        await using var provider = await IssueProviderAsync(browse.Template.IssueProviderId, "Issue provider not found for this template.", ct);
        try
        {
            var issues = await provider.ListOpenIssuesAsync(page, PageSize, NullIfEmpty(labels), ct);
            var stateCache = new Dictionary<int, bool>();
            foreach (var issue in issues.Items)
            {
                ct.ThrowIfCancellationRequested();
                readiness[issue.Identifier] = await dependencyChecker.CheckAsync(issue.Identifier, issue.Description, provider, stateCache, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "TemplateBrowseService: dependency check stopped for template {TemplateId}", browse.Template.Id);
        }
        return readiness;
    }

    /// <summary>The labels of the template's issue tracker, for the drawers' label filter.</summary>
    public async Task<IReadOnlyList<string>> ListLabelsAsync(BrowseTemplate browse, CancellationToken ct)
    {
        await using var provider = await IssueProviderAsync(browse.Template.IssueProviderId, "Issue provider not found for this template.", ct);
        return await CallAsync(() => provider.ListRepositoryLabelsAsync(ct), "Failed to load labels");
    }

    public async Task<PagedResult<PullRequestSummary>> ListPullRequestsAsync(BrowseTemplate browse, int page, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        var config = await providerConfigs.GetProviderConfigByIdAsync(browse.Template.RepoProviderId, ProviderKind.Repository, ct)
            ?? throw new TemplateBrowseException("Repository provider not found for this template.");
        await using var provider = providerFactory.CreateRepositoryProvider(config);
        return await CallAsync(() => provider.ListOpenPullRequestsAsync(page, PageSize, NullIfEmpty(labels), ct), "Failed to load pull requests");
    }

    /// <summary>
    /// The new and the approved epics of the epic tracker: the project's when it has one, otherwise
    /// the template's own. The two lists are merged by identifier.
    /// </summary>
    public async Task<PagedResult<IssueSummary>> ListEpicsAsync(BrowseTemplate browse, int page, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        await using var provider = await IssueProviderAsync(EpicTrackerOf(browse), "Epic issue provider not found.", ct);
        var filter = labels ?? [];
        var epics = await CallAsync(() => provider.ListOpenIssuesAsync(page, EpicPageSize, [AgentLabels.Epic, .. filter], ct), "Failed to load epics");
        var approved = await CallAsync(() => provider.ListOpenIssuesAsync(page, EpicPageSize, [AgentLabels.EpicApproved, .. filter], ct), "Failed to load epics");
        var merged = epics.Items.Concat(approved.Items)
            .GroupBy(i => i.Identifier)
            .Select(g => g.First())
            .ToList();
        return new PagedResult<IssueSummary> { Items = merged, Page = page, PageSize = EpicPageSize, HasMore = epics.HasMore || approved.HasMore };
    }

    /// <summary>The epic tracker's labels without the epic workflow labels, which the drawer filters by itself.</summary>
    public async Task<IReadOnlyList<string>> ListEpicLabelsAsync(BrowseTemplate browse, CancellationToken ct)
    {
        await using var provider = await IssueProviderAsync(EpicTrackerOf(browse), "Epic issue provider not found.", ct);
        var labels = await CallAsync(() => provider.ListRepositoryLabelsAsync(ct), "Failed to load labels");
        return labels.Where(l => !l.StartsWith(AgentLabels.Epic, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The issues and pull requests of the template's trackers and repository that already have work.</summary>
    public Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId, WorkItemStatus Status)>> GetActiveIssuesAsync(BrowseTemplate browse, CancellationToken ct)
    {
        var providerIds = new[] { browse.Template.IssueProviderId, browse.Template.RepoProviderId, EpicTrackerOf(browse) }
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return workItems.GetIssueStatusesAsync(providerIds, ct);
    }

    /// <summary>The tracker the epic drawer lists from: the project's epic tracker, otherwise the template's own.</summary>
    internal static string EpicTrackerOf(BrowseTemplate browse) =>
        !string.IsNullOrEmpty(browse.Project?.EpicIssueProviderId) ? browse.Project.EpicIssueProviderId : browse.Template.IssueProviderId;

    private async Task<IIssueProvider> IssueProviderAsync(string providerId, string missing, CancellationToken ct)
    {
        var config = await providerConfigs.GetProviderConfigByIdAsync(providerId, ProviderKind.Issue, ct)
            ?? throw new TemplateBrowseException(missing);
        return providerFactory.CreateIssueProvider(config);
    }

    private static async Task<T> CallAsync<T>(Func<Task<T>> call, string failure)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TemplateBrowseException($"{failure}: {ex.Message}", ex);
        }
    }

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string>? labels) => labels is { Count: > 0 } ? labels : null;
}
