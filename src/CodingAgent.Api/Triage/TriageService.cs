using System.Text.Json;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.Triage;

/// <summary>A refused triage request: the status code the endpoint returns, and why.</summary>
public sealed class TriageRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The triage operations behind the API's triage endpoints: starting an operator triage, re-running, editing
/// and dismissing, and reading. Issue creation is in <see cref="TriageIssueCreator"/>. Every repository and
/// template id is checked against the triage's own project here, never trusted from the caller.
/// </summary>
public sealed class TriageService
{
    private const int MaxTextChars = 20_000;

    /// <summary>The feedback goes into a tracker comment, which GitHub limits to 65,536 characters.</summary>
    private const int MaxFeedbackChars = 40_000;

    private const int MaxAnswers = 10;
    private const int MaxDraftTitleChars = 200;
    private const int SimilarLookbackDays = 30;
    private const int MaxSimilar = 3;
    private const string NotFound = "Triage not found";

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "at", "for", "is", "are", "was", "after", "before",
        "with", "when", "not", "no", "from", "by", "it", "its", "be", "der", "die", "das", "und",
    };

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ITriageStore _store;
    private readonly IConfigurationStore _config;
    private readonly IOrchestratorRunService _runService;
    private readonly TriageTrackerOperations _tracker;
    private readonly WorkItemRouter _router;
    private readonly ILogger _logger;

    public TriageService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ITriageStore store,
        IConfigurationStore config,
        IOrchestratorRunService runService,
        TriageTrackerOperations tracker,
        WorkItemRouter router,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _store = store;
        _config = config;
        _runService = runService;
        _tracker = tracker;
        _router = router;
        _logger = logger;
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct)
    {
        var page = await _store.ListAsync(query, ct);
        var tokens = await LoadTokensAsync(page.Items.Select(i => i.Facts.LastWorkItemId).OfType<string>(), ct);
        return page with
        {
            Items = page.Items
                .Select(i => i.Facts.LastWorkItemId is { } w && tokens.TryGetValue(w, out var t) ? i with { LatestTokens = t.TotalTokens, LatestUsage = t.Usage } : i)
                .ToList()
        };
    }

    public async Task<TriageDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        var record = await _store.GetAsync(id, ct);
        return record is null ? null : await ToDetailAsync(record, ct);
    }

    /// <summary>The triage a run belongs to, from the run's WorkItem key.</summary>
    public async Task<Guid?> FindByRunAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var key = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == runId && w.TaskType == WorkItemTaskType.Triage)
            .Select(w => new { w.IssueProviderConfigId, w.IssueIdentifier })
            .FirstOrDefaultAsync(ct);
        if (key is null)
        {
            // The WorkItem may be gone: search the attempts of the run's project instead
            var run = await db.PipelineRuns.AsNoTracking()
                .Where(r => r.RunId == runId)
                .Select(r => new { r.IssueProviderConfigId, r.IssueIdentifier })
                .FirstOrDefaultAsync(ct);
            if (run?.IssueProviderConfigId is null)
                return null;
            key = new { run.IssueProviderConfigId, run.IssueIdentifier };
        }

        return await db.Triages.AsNoTracking()
            .Where(t => t.KeyProviderConfigId == key.IssueProviderConfigId && t.KeyIdentifier == key.IssueIdentifier)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The project's recent triages whose titles share a word with <paramref name="title"/>.</summary>
    public async Task<IReadOnlyList<TriageListItem>> FindSimilarAsync(string projectId, string title, CancellationToken ct)
    {
        var words = Words(title);
        if (words.Count == 0)
            return [];

        var page = await _store.ListAsync(new TriageListQuery { ProjectId = projectId, PageSize = 200 }, ct);
        var since = DateTimeOffset.UtcNow.AddDays(-SimilarLookbackDays);
        return page.Items
            .Where(i => i.UpdatedAt >= since)
            .Select(i => (Item: i, Shared: Words(i.Title).Count(words.Contains)))
            .Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared).ThenByDescending(x => x.Item.UpdatedAt)
            .Take(MaxSimilar)
            .Select(x => x.Item)
            .ToList();
    }

    // ── Starting and re-running ──────────────────────────────────────────────

    /// <summary>Starts an operator triage: its row and its first WorkItem, in one transaction.</summary>
    public async Task<TriageDetail> CreateAsync(CreateTriageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = Validate(request.Request);
        var project = await _config.GetProjectByIdAsync(request.ProjectId, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, "Project not found");
        if (!project.Enabled)
            throw new TriageRequestException(StatusCodes.Status400BadRequest, "The project is disabled");

        var executor = await ResolveExecutorAsync(project, form.StartInTemplateId, ct);
        var now = DateTimeOffset.UtcNow;
        var workItemId = Guid.NewGuid();
        var record = new TriageRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Source = TriageSource.Operator,
            Title = form.Title,
            Request = form,
            RequestedBy = request.RequestedBy,
            Attempts = [new TriageAttempt { WorkItemId = workItemId.ToString(), StartedAt = now }],
            CreatedAt = now,
            UpdatedAt = now,
        };

        var distribution = await BuildDistributionRequestAsync(record, project, executor, workItemId, ct);
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            db.Triages.Add(TriageEntityMapper.ToEntity(record));
            db.WorkItems.Add(WorkItemEntityBuilder.BuildWorkItemEntity(distribution, workItemId));
            await db.SaveChangesAsync(ct);
        }

        AddRun(workItemId, distribution);
        _logger.Information("Started operator triage {TriageId} in project {ProjectId} on template {Template} (run {RunId})",
            record.Id, project.Id, executor.Name, workItemId);
        return await ToDetailAsync(record, ct);
    }

    /// <summary>
    /// Re-runs a triage with feedback. An operator triage gets a new attempt and WorkItem at once; a tracker
    /// triage gets the feedback as an issue comment and <c>agent:triage</c>, and the loop picks it up.
    /// </summary>
    public async Task<TriageDetail> RerunAsync(Guid id, RerunTriageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = await _store.GetAsync(id, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);
        if (await _store.GetActiveWorkItemIdAsync(record, ct) is not null)
            throw new TriageRequestException(StatusCodes.Status409Conflict, "An attempt is already running");

        var feedback = new TriageFeedback
        {
            Text = Cut(request.Feedback),
            Answers = request.Answers
                .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                .Take(MaxAnswers)
                .Select(a => new TriageAnswer { Question = Cut(a.Question) ?? "", Answer = Cut(a.Answer) ?? "" })
                .ToList(),
            Author = request.Author,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if ((feedback.Text?.Length ?? 0) + feedback.Answers.Sum(a => a.Question.Length + a.Answer.Length) > MaxFeedbackChars)
            throw new TriageRequestException(StatusCodes.Status400BadRequest,
                $"The feedback is too long (at most {MaxFeedbackChars} characters with the answers)");

        if (record.Source == TriageSource.Issue)
        {
            try
            {
                await _tracker.RequestRerunAsync(record, feedback, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TriageRequestException)
            {
                _logger.Warning(ex, "Re-run of triage {TriageId} could not update its issue", id);
                throw new TriageRequestException(StatusCodes.Status502BadGateway,
                    $"The issue could not be updated, so no attempt was queued: {ex.Message}");
            }
            return await ToDetailAsync(record, ct);
        }

        var project = await _config.GetProjectByIdAsync(record.ProjectId, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, "The triage's project no longer exists");
        var executor = await ResolveExecutorAsync(project, record.Request?.StartInTemplateId, ct, startInIsHint: true);
        var workItemId = Guid.NewGuid();
        var distribution = await BuildDistributionRequestAsync(record, project, executor, workItemId, ct);

        TriageRecord updated;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var entity = await db.Triages.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);
            var current = TriageEntityMapper.ToRecord(entity);
            updated = current with
            {
                Attempts = [.. current.Attempts, new TriageAttempt
                {
                    WorkItemId = workItemId.ToString(),
                    StartedAt = DateTimeOffset.UtcNow,
                    Feedback = feedback,
                }],
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            TriageEntityMapper.CopyInto(updated, entity);
            db.WorkItems.Add(WorkItemEntityBuilder.BuildWorkItemEntity(distribution, workItemId));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent re-run (the partial unique index on WorkItems) or a concurrent edit (xmin)
                _logger.Warning(ex, "Re-run of triage {TriageId} conflicted with a concurrent change", id);
                throw new TriageRequestException(StatusCodes.Status409Conflict, "The triage changed or an attempt started meanwhile; reload and try again");
            }
        }

        AddRun(workItemId, distribution);
        _logger.Information("Re-ran triage {TriageId} (run {RunId})", id, workItemId);
        return await ToDetailAsync(updated, ct);
    }

    // ── Drafts and dismissal ─────────────────────────────────────────────────

    public async Task<TriageDetail> UpdateDraftAsync(Guid id, string draftId, UpdateTriageDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            throw new TriageRequestException(StatusCodes.Status400BadRequest, "A draft needs a title and a body");
        if (request.Title.Length > MaxDraftTitleChars || request.Body.Length > TriageConstants.MaxDraftBodyChars)
            throw new TriageRequestException(StatusCodes.Status400BadRequest,
                $"A draft's title has at most {MaxDraftTitleChars} and its body at most {TriageConstants.MaxDraftBodyChars} characters");

        var record = await _store.GetAsync(id, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);
        var repository = await ResolveRepositoryNameAsync(record.ProjectId, request.TargetRepository, ct);

        var saved = await _store.UpdateAsync(id, r =>
        {
            var drafts = r.Drafts.ToList();
            var index = drafts.FindIndex(d => d.Current.Id == draftId);
            if (index < 0)
                throw new TriageRequestException(StatusCodes.Status404NotFound, "Draft not found");
            if (r.CreatedIssues.Any(c => c.DraftId == draftId))
                throw new TriageRequestException(StatusCodes.Status409Conflict, "The draft's issue was already created");

            drafts[index] = drafts[index] with
            {
                Current = drafts[index].Current with
                {
                    Kind = request.Kind,
                    TargetRepository = repository,
                    Title = request.Title.Trim(),
                    Body = request.Body,
                    Warning = null,
                },
                EditedBy = request.EditedBy,
                EditedAt = DateTimeOffset.UtcNow,
            };
            return r with { Drafts = drafts };
        }, ct) ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);

        return await ToDetailAsync(saved, ct);
    }

    public async Task<TriageDetail> ResetDraftAsync(Guid id, string draftId, CancellationToken ct)
    {
        var saved = await _store.UpdateAsync(id, r =>
        {
            var drafts = r.Drafts.ToList();
            var index = drafts.FindIndex(d => d.Current.Id == draftId);
            if (index < 0)
                throw new TriageRequestException(StatusCodes.Status404NotFound, "Draft not found");
            drafts[index] = drafts[index] with { Current = drafts[index].Original, EditedBy = null, EditedAt = null };
            return r with { Drafts = drafts };
        }, ct) ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);
        return await ToDetailAsync(saved, ct);
    }

    public async Task<TriageDetail> DismissAsync(Guid id, DismissTriageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = await _store.GetAsync(id, ct)
            ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);
        if (await _store.GetActiveWorkItemIdAsync(record, ct) is not null)
            throw new TriageRequestException(StatusCodes.Status409Conflict, "An attempt is running; cancel it first");

        var saved = await _store.UpdateAsync(id, r => r with
        {
            State = TriageState.Dismissed,
            DismissReason = Cut(request.Reason),
            DismissedBy = request.By,
        }, ct) ?? throw new TriageRequestException(StatusCodes.Status404NotFound, NotFound);

        if (saved.Source == TriageSource.Issue)
            await _tracker.ReportDismissedAsync(saved, ct);

        return await ToDetailAsync(saved, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The template that runs an operator triage: the "start looking in" template when it is an enabled template
    /// of the project, else the project's first enabled template by name.
    /// </summary>
    /// <param name="startInIsHint">
    /// True for a re-run: the stored "start looking in" template may have been disabled since, and the triage falls
    /// back to the first enabled template instead of failing.
    /// </param>
    internal async Task<PipelineJobTemplate> ResolveExecutorAsync(
        PipelineProject project, string? startInTemplateId, CancellationToken ct, bool startInIsHint = false)
    {
        var enabled = (await _config.LoadTemplatesForProjectAsync(project.Id, ct)).Where(t => t.Enabled).ToList();
        if (enabled.Count == 0)
            throw new TriageRequestException(StatusCodes.Status400BadRequest, "The project has no enabled template to run a triage");

        if (!string.IsNullOrEmpty(startInTemplateId))
        {
            var startIn = enabled.FirstOrDefault(t => t.Id == startInTemplateId);
            if (startIn is not null)
                return startIn;
            if (!startInIsHint)
                throw new TriageRequestException(StatusCodes.Status400BadRequest,
                    "\"Start looking in\" must be an enabled repository of the triage's project");
        }

        return TemplateOrder.ByName(enabled, t => t.Name, t => t.Id).First();
    }

    /// <summary>The canonical name of an enabled repository (template) of the project, or 400.</summary>
    internal async Task<string> ResolveRepositoryNameAsync(string projectId, string repository, CancellationToken ct)
    {
        var enabled = (await _config.LoadTemplatesForProjectAsync(projectId, ct)).Where(t => t.Enabled);
        return enabled.FirstOrDefault(t => string.Equals(t.Name, repository?.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
            ?? throw new TriageRequestException(StatusCodes.Status400BadRequest,
                $"'{repository}' is not an enabled repository of the triage's project");
    }

    private async Task<JobDistributionRequest> BuildDistributionRequestAsync(
        TriageRecord record, PipelineProject project, PipelineJobTemplate executor, Guid workItemId, CancellationToken ct)
    {
        _ = await _config.GetProviderConfigByIdAsync(executor.RepoProviderId, ProviderKind.Repository, ct)
            ?? throw new TriageRequestException(StatusCodes.Status400BadRequest,
                $"The repository of template '{executor.Name}' is not configured");
        var globalConfig = await _config.LoadPipelineConfigAsync(ct);
        var config = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);

        var request = new JobDistributionRequest
        {
            IssueIdentifier = TriageConstants.IssueIdentifierFor(record.Id),
            IssueProviderConfigId = TriageConstants.ProviderConfigId,
            RepoProviderConfigId = executor.RepoProviderId,
            BrainProviderConfigId = executor.BrainProviderId,
            PipelineProviderConfigId = executor.PipelineProviderId,
            InitiatedBy = InitiatedByConstants.Manual,
            TaskType = WorkItemTaskType.Triage,
            RunType = PipelineRunType.Triage,
            TimeoutSeconds = (int)config.AgentTimeout.TotalSeconds,
            ProjectId = Guid.TryParse(project.Id, out var pid) ? pid : null,
            ProjectName = project.Name,
            RunId = workItemId.ToString(),
            IssueDetail = new IssueDetail
            {
                Identifier = TriageConstants.IssueIdentifierFor(record.Id),
                Title = record.Title,
                Description = "",
                Labels = [],
            },
        };

        // Spec 053 req 4.2: the API routes the triage like every other work item; a refusal is the start's 400.
        var routed = await _router.RouteAsync(request, PipelineTelemetry.RoutingStages.Create, ct);
        return routed.Request ?? throw new TriageRequestException(StatusCodes.Status400BadRequest, routed.RefusalText!);
    }

    private void AddRun(Guid workItemId, JobDistributionRequest distribution)
    {
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, distribution);
        if (run is not null)
            _runService.AddRun(run);
    }

    private async Task<TriageDetail> ToDetailAsync(TriageRecord record, CancellationToken ct)
    {
        var activeId = await _store.GetActiveWorkItemIdAsync(record, ct);
        var runs = await LoadTokensAsync(record.Attempts.Select(a => a.WorkItemId), ct);

        // How an attempt without a result ended is copied to the record by the hourly sweep; until then its run
        // record tells (only shown, not stored)
        if (activeId is null && record.Attempts.Count > 0 && record.Attempts[^1] is { Result: null, Outcome: null } last
            && runs.TryGetValue(last.WorkItemId, out var run) && run.FinalStep is PipelineStep.Cancelled or PipelineStep.Failed)
        {
            record = record with
            {
                Attempts = [.. record.Attempts.Take(record.Attempts.Count - 1), last with
                {
                    Outcome = run.FinalStep == PipelineStep.Cancelled ? TriageAttemptOutcome.Cancelled : TriageAttemptOutcome.Failed,
                    FailureReason = last.FailureReason ?? run.FailureReason,
                }],
            };
        }

        return new TriageDetail
        {
            Record = record,
            Status = TriageStatusResolver.Resolve(record, activeId is not null),
            ActiveWorkItemId = activeId,
            AttemptRuns = runs,
        };
    }

    /// <summary>Run facts of the given run ids, from the run records that still exist.</summary>
    private async Task<Dictionary<string, TriageAttemptRunInfo>> LoadTokensAsync(IEnumerable<string> runIds, CancellationToken ct)
    {
        var ids = runIds.Select(r => Guid.TryParse(r, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PipelineRuns.AsNoTracking()
            .Where(r => ids.Contains(r.RunId))
            .Select(r => new { r.RunId, r.FinalStep, r.StartedAt, r.CompletedAt, r.SummaryJson })
            .ToListAsync(ct);

        var result = new Dictionary<string, TriageAttemptRunInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            PipelineRunSummary? summary = null;
            if (row.SummaryJson is not null)
            {
                try { summary = JsonSerializer.Deserialize<PipelineRunSummary>(row.SummaryJson, PipelineJsonOptions.Lenient); }
                catch (JsonException) { /* an unreadable summary only loses the token count */ }
            }
            result[row.RunId.ToString()] = new TriageAttemptRunInfo
            {
                TotalTokens = summary?.TotalTokens,
                Usage = summary is null ? null : RunUsage.From(summary),
                FinalStep = row.FinalStep,
                FailureReason = summary?.FailureReason,
                StartedAt = row.StartedAt,
                CompletedAt = row.CompletedAt,
            };
        }
        return result;
    }

    private static TriageRequest Validate(TriageRequest? request)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.WhatHappened)
            || string.IsNullOrWhiteSpace(request.Expected))
            throw new TriageRequestException(StatusCodes.Status400BadRequest, "Title, what happened and expected are required");

        return request with
        {
            Title = TextSanitizer.SanitizeTitle(request.Title),
            WhatHappened = Cut(request.WhatHappened)!,
            Expected = Cut(request.Expected)!,
            Environment = Cut(request.Environment),
            Where = Cut(request.Where),
            Version = Cut(request.Version),
            Links = Cut(request.Links),
            AlreadyTried = Cut(request.AlreadyTried),
        };
    }

    private static string? Cut(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Length <= MaxTextChars ? value.Trim() : value[..MaxTextChars];
    }

    private static HashSet<string> Words(string text) =>
        text.Split([' ', '-', '_', '.', ',', ':', ';', '/', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
}
