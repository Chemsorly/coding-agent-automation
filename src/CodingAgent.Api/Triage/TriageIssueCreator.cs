using System.Globalization;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.Triage;

/// <summary>
/// Creates issues from a triage's drafts, as a person approved them in the app. Never an agent: the drafts are
/// already complete, and creating them is the human gate. One advisory lock per triage makes retries and double
/// clicks safe; each created issue is recorded right after it is created, so a draft is never created twice.
/// </summary>
public sealed class TriageIssueCreator
{
    private readonly ITriageStore _store;
    private readonly IConfigurationStore _config;
    private readonly IProviderFactory _providers;
    private readonly IDistributedLockProvider _locks;
    private readonly TriageTrackerOperations _tracker;
    private readonly ILogger _logger;

    public TriageIssueCreator(
        ITriageStore store,
        IConfigurationStore config,
        IProviderFactory providers,
        IDistributedLockProvider locks,
        TriageTrackerOperations tracker,
        ILogger logger)
    {
        _store = store;
        _config = config;
        _providers = providers;
        _locks = locks;
        _tracker = tracker;
        _logger = logger;
    }

    public async Task<CreateTriageIssuesResult> CreateAsync(Guid triageId, CreateTriageIssuesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DraftIds.Count == 0)
            throw new TriageRequestException(StatusCodes.Status400BadRequest, "Select at least one draft");

        await using var held = await _locks.AcquireAsync($"triage-create-issues:{triageId:D}", ct);

        var triage = await _store.GetAsync(triageId, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, "Triage not found");
        if (await _store.GetActiveWorkItemIdAsync(triage, ct) is not null)
            throw new TriageRequestException(StatusCodes.Status409Conflict, "An attempt is running; wait for its result");

        var project = await _config.GetProjectByIdAsync(triage.ProjectId, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, "The triage's project no longer exists");
        var templates = (await _config.LoadTemplatesForProjectAsync(project.Id, ct))
            .Where(t => t.Enabled)
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var created = new List<TriageCreatedIssue>();
        var createdNow = 0;
        var errors = new List<TriageDraftError>();
        // The attempt as the triage page numbers it: its position among all attempts
        var draftsAttempt = triage.Attempts.ToList().FindIndex(a => a.WorkItemId == triage.DraftsFromWorkItemId);
        var attemptNumber = draftsAttempt >= 0 ? draftsAttempt + 1 : triage.Attempts.Count;
        var secrets = await TriageSecrets.LoadAsync(_config, project.Id, ct);

        foreach (var draftId in request.DraftIds.Distinct(StringComparer.Ordinal))
        {
            var editable = triage.Drafts.FirstOrDefault(d => d.Current.Id == draftId);
            if (editable is null)
            {
                errors.Add(new TriageDraftError { DraftId = draftId, Message = "No such draft" });
                continue;
            }

            if (triage.CreatedIssues.FirstOrDefault(c => c.DraftId == draftId) is { } existing)
            {
                created.Add(existing);
                continue;
            }

            var draft = editable.Current;
            if (!templates.TryGetValue(draft.TargetRepository, out var template))
            {
                errors.Add(new TriageDraftError
                {
                    DraftId = draftId,
                    Message = $"'{draft.TargetRepository}' is not an enabled repository of the project",
                });
                continue;
            }

            ct.ThrowIfCancellationRequested();
            CreatedIssueResult issue;
            try
            {
                // Not cancellable from here on: an issue created on the tracker must also be recorded
                issue = await CreateIssueAsync(triage, secrets, template, draft, attemptNumber, request.Queue, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Could not create the issue of draft {DraftId} of triage {TriageId}", draftId, triageId);
                errors.Add(new TriageDraftError { DraftId = draftId, Message = $"The issue could not be created: {ex.Message}" });
                continue;
            }

            try
            {
                var record = new TriageCreatedIssue
                {
                    DraftId = draftId,
                    Repository = template.Name,
                    IssueProviderConfigId = template.IssueProviderId,
                    Identifier = issue.Identifier,
                    Url = issue.Url,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CreatedBy = request.CreatedBy,
                };

                // Record it before the next one: a retry after a crash must not create it again
                triage = await _store.UpdateAsync(triageId, r => r with
                {
                    CreatedIssues = [.. r.CreatedIssues, record],
                    State = TriageState.IssuesCreated,
                }, CancellationToken.None) ?? triage;
                created.Add(record);
                createdNow++;

                _logger.Information("Created issue {Identifier} in {Repository} from draft {DraftId} of triage {TriageId}",
                    issue.Identifier, template.Name, draftId, triageId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Created issue {Identifier} from draft {DraftId} of triage {TriageId}, but could not record it",
                    issue.Identifier, draftId, triageId);
                errors.Add(new TriageDraftError
                {
                    DraftId = draftId,
                    Message = $"Issue #{issue.Identifier} was created ({issue.Url}) but could not be recorded: do not create this draft again",
                });
            }
        }

        if (triage.Source == TriageSource.Issue && createdNow > 0)
            await _tracker.ReportCreatedAsync(triage, triage.CreatedIssues, ct);

        return new CreateTriageIssuesResult { Created = created, Errors = errors };
    }

    private async Task<CreatedIssueResult> CreateIssueAsync(
        TriageRecord triage, IReadOnlyCollection<KeyValuePair<string, string>> secrets, PipelineJobTemplate template,
        TriageDraft draft, int attemptNumber, bool queue, CancellationToken ct)
    {
        var issueConfig = await _config.GetProviderConfigByIdAsync(template.IssueProviderId, ProviderKind.Issue, ct)
            ?? throw new InvalidOperationException($"The tracker of '{template.Name}' is not configured");

        // An operator triage's title is private text: the footer names it only by id. A tracker triage links its
        // issue, which is already where the report was made.
        var footer = string.Format(CultureInfo.InvariantCulture,
            "\n\n---\n_Proposed by {0} (attempt {1}) and approved in Coding Agent._",
            triage.IssueUrl is { } url ? $"the triage of {url}" : $"Coding Agent triage {triage.Id.ToString("D")[..8]}",
            attemptNumber);

        // Mask the raw text first: sanitizing changes characters, so a secret would no longer match
        var body = TextSanitizer.SanitizeMarkdown(SecretMasker.Mask(draft.Body, secrets)) + footer;
        var title = TextSanitizer.SanitizeTitle(SecretMasker.Mask(draft.Title, secrets));

        IReadOnlyList<string> labels = queue ? [AgentLabels.Generated, AgentLabels.Next] : [AgentLabels.Generated];

        await using var provider = _providers.CreateIssueProvider(issueConfig);
        return await provider.CreateIssueAsync(title, body, labels, ct);
    }
}
