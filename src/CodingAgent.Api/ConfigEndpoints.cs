using System.Text.Json;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using Serilog;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace CodingAgent.Api;

/// <summary>
/// Minimal API endpoints for config CRUD operations.
/// All endpoints require the OperatorApiKey authorization policy.
/// Secret redaction is applied on all GET endpoints returning ProviderConfig.
/// GET /api/config/export and POST /api/config/import are guarded by OperatorApiKey (Tier 2).
/// </summary>
public static class ConfigEndpoints
{
    /// <summary>Valid secret key names: must start with a letter or underscore, followed by letters, digits, underscores.</summary>
    private static readonly Regex SecretKeyPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled, matchTimeout: TimeSpan.FromSeconds(1));

    /// <summary>The 400 for an agent provider config read or write: agent configs are built per route (spec 053).</summary>
    internal const string AgentProviderConfigsRetired =
        "Agent provider configs no longer exist: the tool settings are on the agent profile (Settings → Agents → Agent Profiles).";

    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };


    /// <summary>
    /// Maps all config endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // Spec 051: Settings writes (provider configs and templates included), the model fetch and the first-run
        // state are not mapped here; the web host makes them as the signed-in admin through
        // /api/ui/settings (UiSettingsEndpoints, UiProviderEndpoints), which reuses these handlers. What stays is what the Scheduler still reads or writes with
        // the API key, plus export and import, which operators script with the key (docs/bootstrap.md).
        var group = app.MapGroup("/api/config")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        // ── Config import/export — OperatorApiKey Tier 2 (agent pods cannot overwrite config)
        group.MapGet("/export", ExportConfigAsync);
        group.MapPost("/import", ImportConfigAsync)
            .DisableAntiforgery();

        // ── Pipeline config ─────────────────────────────────────────────
        group.MapGet("/pipeline", GetPipelineConfig);

        // ── Provider configs ────────────────────────────────────────────
        group.MapGet("/provider-configs", GetProviderConfigs);

        // ── Quality gate configs ────────────────────────────────────────
        group.MapGet("/quality-gate-configs", GetQualityGateConfigs);

        // ── Reviewer configs ────────────────────────────────────────────
        group.MapGet("/reviewer-configs", GetReviewerConfigs);

        // ── Projects (requires two store calls — Req 6.3a) ─────────────
        group.MapGet("/projects", GetProjects);

        // ── Templates ───────────────────────────────────────────────────
        group.MapGet("/templates", GetAllTemplates);
    }

    // ── Pipeline config ────────────────────────────────────────────────────

    internal static async Task<IResult> GetPipelineConfig(
        IPipelineConfigStore store, CancellationToken ct)
    {
        var config = await store.LoadPipelineConfigAsync(ct);
        return TypedResults.Ok(config);
    }

    // ── Provider configs ───────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/provider-configs?kind={kind}&amp;includeSecrets={bool}
    /// Returns all configs of the given kind. Settings and Secrets values are redacted unless
    /// <paramref name="includeSecrets"/> is explicitly requested.
    ///
    /// The opt-in exists because two very different callers share this endpoint. The Blazor
    /// settings pages render these configs and must never receive live credentials — they use
    /// the redacted default. The monolith's dispatch path reads the same configs through
    /// <c>ApiConfigurationStore</c> and embeds them in the job payload the agent receives;
    /// <c>RunEnvironmentSetupStep</c> writes <c>Secrets</c> straight into the run environment and
    /// the provider resolvers read tokens and base URLs out of <c>Settings</c>. Serving that path
    /// redacted ships every job with "****" in place of its credentials.
    ///
    /// The whole /api/config group already requires the operator key, so this is defence in depth
    /// for the UI rather than a trust boundary — an agent-derived key cannot reach either form.
    /// </summary>
    internal static async Task<IResult> GetProviderConfigs(
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct,
        bool includeSecrets = false)
    {
        if (kind == ProviderKind.Agent)
            return TypedResults.BadRequest(AgentProviderConfigsRetired);
        var configs = await store.LoadProviderConfigsAsync(kind, ct);
        if (includeSecrets)
            return TypedResults.Ok(configs.ToList());

        return TypedResults.Ok(configs.Select(RedactProviderConfig).ToList());
    }

    /// <summary>Saves the config as given; the UI handler restores masked credentials first (<c>UiProviderEndpoints</c>).</summary>
    internal static async Task<IResult> SaveProviderConfig(
        [FromBody] ProviderConfig config,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        if (config.Kind == ProviderKind.Repository)
        {
            // Repository settings live on the pipeline job template, not the connection.
            var hasRepoSettings = config.RequiredLabels is { Count: > 0 }
                || config.Secrets is { Count: > 0 }
                || config.SetupSteps is { Count: > 0 }
                || !string.IsNullOrEmpty(config.SteeringContent)
                || config.BlacklistedPaths is { Count: > 0 };
            if (hasRepoSettings)
                return TypedResults.BadRequest(
                    "Agent labels, secrets, setup steps, steering and the commit blacklist are set on the pipeline job template.");
        }

        // Spec 053 req 7.13: the agent config is built from the profile and its route; a previous-release tab
        // must fail rather than store one.
        if (config.Kind == ProviderKind.Agent)
            return TypedResults.BadRequest(AgentProviderConfigsRetired);
        return await SaveAsync(() => store.SaveProviderConfigAsync(config, ct));
    }

    internal static async Task<IResult> DeleteProviderConfig(
        string id,
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteProviderConfigAsync(id, kind, ct);
        return TypedResults.Ok();
    }

    // ── Agent profiles ─────────────────────────────────────────────────────

    internal static async Task<IResult> SaveAgentProfile(
        [FromBody] AgentProfile profile,
        IAgentProfileStore store,
        JobTemplateStore templates,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(profile.JobTemplate) && templates.GetAllTemplates().Count > 0)
        {
            var key = profile.JobTemplateKey();
            if (templates.Resolve(key) is null)
                // TODO: [WARNING] This BadRequest branch has no integration test. The issue test
                // requirements list "An unknown key returns 400 when the template store has templates".
                // Without a test, inverting the condition or changing the response code would go
                // undetected by CI. See ConfigEndpointTests.cs for the tracking TODO.
                return TypedResults.BadRequest($"Job template [{key}] does not exist in the Helm values (jobTemplates).");
        }

        return await SaveAsync(() => store.SaveAgentProfileAsync(profile, ct));
    }

    internal static async Task<IResult> DeleteAgentProfile(
        string id,
        IAgentProfileStore store,
        CancellationToken ct)
    {
        await store.DeleteAgentProfileAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Quality gate configs ───────────────────────────────────────────────

    internal static async Task<IResult> GetQualityGateConfigs(
        IQualityGateConfigStore store, CancellationToken ct)
    {
        var configs = await store.LoadQualityGateConfigsAsync(ct);
        return TypedResults.Ok(configs);
    }

    internal static async Task<IResult> SaveQualityGateConfig(
        [FromBody] QualityGateConfiguration config,
        IQualityGateConfigStore store,
        CancellationToken ct)
    {
        return await SaveAsync(() => store.SaveQualityGateConfigAsync(config, ct));
    }

    internal static async Task<IResult> DeleteQualityGateConfig(
        string id,
        IQualityGateConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteQualityGateConfigAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Reviewer configs ───────────────────────────────────────────────────

    internal static async Task<IResult> GetReviewerConfigs(
        IReviewerConfigStore store, CancellationToken ct)
    {
        var configs = await store.LoadReviewerConfigsAsync(ct);
        return TypedResults.Ok(configs);
    }

    internal static async Task<IResult> SaveReviewerConfig(
        [FromBody] ReviewerConfiguration config,
        IReviewerConfigStore store,
        CancellationToken ct)
    {
        return await SaveAsync(() => store.SaveReviewerConfigAsync(config, ct));
    }

    internal static async Task<IResult> DeleteReviewerConfig(
        string id,
        IReviewerConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteReviewerConfigAsync(id, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> ResetReviewerConfigs(
        IReviewerConfigStore store, CancellationToken ct)
    {
        await store.ResetReviewerConfigsToDefaultAsync(ct);
        return TypedResults.Ok();
    }

    // ── Projects ───────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/projects
    /// Returns all projects with their templates joined in (Req 6.3a).
    /// Each project's TemplateIds are filled from its templates' own project, in TemplateOrder.
    /// Returning projects with empty template lists is a bug, not a simplification.
    /// </summary>
    internal static async Task<IResult> GetProjects(
        IProjectStore store, CancellationToken ct)
    {
        var projects = await store.LoadProjectsAsync(ct);
        return TypedResults.Ok(projects);
    }

    /// <summary>
    /// PUT /api/config/projects. Rejects overrides outside the range of the setting they override (400, one sentence
    /// per override), so a bad value never reaches the resolver.
    /// </summary>
    internal static async Task<IResult> SaveProject(
        [FromBody] PipelineProject project,
        IProjectStore store,
        CancellationToken ct)
    {
        var errors = PipelineSettingsValidator.ValidateOverrides(project);
        if (errors.Count > 0)
            return TypedResults.BadRequest(string.Join(" ", errors));

        return await SaveAsync(() => store.SaveProjectAsync(project, ct));
    }

    internal static async Task<IResult> DeleteProject(
        string id,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.DeleteProjectAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Templates ──────────────────────────────────────────────────────────

    internal static Task<IResult> MoveTemplateFlat(
        [FromBody] MoveTemplateFlatRequest request,
        IProjectStore store,
        CancellationToken ct)
        => MoveTemplateToProjectAsync(request.SourceProjectId, request.TargetProjectId, request.TemplateId, store, ct);

    internal static async Task<IResult> GetAllTemplates(
        IProjectStore projectStore,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        // Fetch all project IDs then load templates per project (reuses store deserialization logic)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectIds = await db.Projects.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var all = new List<PipelineJobTemplate>();
        foreach (var pid in projectIds)
        {
            var templates = await projectStore.LoadTemplatesForProjectAsync(pid.ToString(), ct);
            all.AddRange(templates);
        }
        return TypedResults.Ok(all);
    }

    internal static async Task<IResult> SaveTemplate(
        string projectId,
        [FromBody] PipelineJobTemplate template,
        IProjectStore store,
        CancellationToken ct)
    {
        if (await store.GetProjectByIdAsync(projectId, ct) is null)
            return TypedResults.NotFound($"Project {projectId} does not exist.");

        // Spec 053: a body without routes keeps the stored ones, so a page that does not edit routes cannot drop rows
        // saved after it loaded; an empty dictionary clears them.
        if (template.AgentRoutes is null)
        {
            var stored = (await store.LoadTemplatesForProjectAsync(projectId, ct)).FirstOrDefault(t => t.Id == template.Id);
            if (stored?.AgentRoutes is not null)
                template = template with { AgentRoutes = stored.AgentRoutes };
        }

        var conflict = await FindTemplateConflictAsync(template, projectId, store, ct);
        if (conflict is not null)
            return TypedResults.BadRequest(conflict);

        // Validate secret key names: must match ^[A-Za-z_][A-Za-z0-9_]*$ (same rule as project secrets).
        if (template.Secrets is { Count: > 0 })
        {
            // TODO: [WARNING] No server-side check for duplicate secret keys. Because Secrets is a
            // Dictionary<string,string>, duplicate keys in the JSON payload are silently resolved by the
            // serializer (last value wins) rather than reported as a validation error. The prior UI guard
            // in RepoProviderSection.razor ("Duplicate key.") was removed and not reintroduced on the
            // template path. If the client-side guard in RepositorySettingsEditor.razor is bypassed,
            // users get no feedback about duplicates. Consider adding a duplicate-key check here.
            foreach (var key in template.Secrets.Keys)
            {
                if (!SecretKeyPattern.IsMatch(key))
                    return TypedResults.BadRequest(
                        $"Secret name '{key}' is invalid; names must start with a letter or underscore and contain only letters, digits and underscores.");
            }
        }

        // Validate setup steps: each step must have both a name and a command.
        if (template.SetupSteps is { Count: > 0 })
        {
            foreach (var step in template.SetupSteps)
            {
                if (string.IsNullOrWhiteSpace(step.Name) || string.IsNullOrWhiteSpace(step.Command))
                    return TypedResults.BadRequest("Each setup step must have a name and a command.");
            }
        }

        return await SaveAsync(() => store.SaveTemplateAsync(projectId, template, ct));
    }

    internal static async Task<IResult> DeleteTemplate(
        string projectId,
        string templateId,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.DeleteTemplateAsync(projectId, new TemplateId(templateId), ct);
        return TypedResults.Ok();
    }

    private static async Task<IResult> MoveTemplateToProjectAsync(
        string sourceProjectId,
        string targetProjectId,
        string templateId,
        IProjectStore store,
        CancellationToken ct)
    {
        if (await store.GetProjectByIdAsync(targetProjectId, ct) is null)
            return TypedResults.NotFound($"Project {targetProjectId} does not exist.");

        var template = (await store.LoadAllTemplatesAsync(ct)).FirstOrDefault(t => t.Id == templateId);
        if (template is not null)
        {
            var conflict = await FindTemplateConflictAsync(template, targetProjectId, store, ct);
            if (conflict is not null)
                return TypedResults.BadRequest(conflict);
        }

        await store.MoveTemplateAsync(sourceProjectId, targetProjectId, new TemplateId(templateId), ct);
        return TypedResults.Ok();
    }

    /// <summary>
    /// Why the template cannot be saved into the project (<see cref="TemplateBindingRules"/>), or <c>null</c>.
    /// </summary>
    private static async Task<string?> FindTemplateConflictAsync(
        PipelineJobTemplate template, string projectId, IProjectStore store, CancellationToken ct)
    {
        var templates = await store.LoadAllTemplatesAsync(ct);
        var projects = await store.LoadProjectsAsync(ct);
        return TemplateBindingRules.Validate(template, projectId, templates, projects);
    }

    // ── Config import/export ───────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/export
    /// Returns a JSON file download containing all config (providers, profiles, gate configs, etc.).
    /// Requires OperatorApiKey policy (042 Req 6.5, Tier 2) — agent-derived keys receive 403.
    ///
    /// Provider Settings and Secrets are exported UNREDACTED, matching the monolith endpoint this
    /// replaced. Export/import is a backup-and-restore path: <see cref="ImportConfigAsync"/> writes
    /// the bundle verbatim, so a redacted export would restore every credential as a mask string and
    /// silently break every provider. The 042 Req 6.4 redaction applies to the read endpoints
    /// (<c>GET /api/config/providers*</c>), which is where secrets must never surface — see
    /// <see cref="RedactProviderConfig"/>.
    ///
    /// The response therefore contains live credentials. It is guarded by the operator tier and the
    /// UI warns before download; treat the downloaded file as a secret.
    /// </summary>
    internal static async Task<IResult> ExportConfigAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bundle = new ConfigBundle
        {
            PipelineConfig = await LoadFirstEntityJson(db.PipelineConfig, e => e.Configuration, ct),
            ProviderConfigs = await db.ProviderConfigs.AsNoTracking().Select(e => new ProviderConfigDto
            {
                Id = e.Id,
                Kind = e.Kind,
                DisplayName = e.DisplayName,
                ProviderType = e.ProviderType,
                Enabled = e.Enabled,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            AgentProfiles = await db.AgentProfiles.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            QualityGateConfigs = await db.QualityGateConfigs.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            ReviewerConfigs = await db.ReviewerConfigs.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            Projects = await db.Projects.AsNoTracking().Select(e => new ProjectDto
            {
                Id = e.Id,
                Name = e.Name,
                Enabled = e.Enabled,
                Description = e.Description,
                Settings = e.Settings
            }).ToListAsync(ct),
            JobTemplates = await db.PipelineJobTemplates.AsNoTracking().Select(e => new JobTemplateDto
            {
                Id = e.Id,
                ProjectId = e.ProjectId,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            AgentRouting = await db.KeyValueStore.AsNoTracking()
                .Where(kv => kv.Key == AgentRouting.StorageKey)
                .Select(kv => kv.Value)
                .FirstOrDefaultAsync(ct)
        };

        var json = JsonSerializer.Serialize(bundle, ExportOptions);
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(json),
            "application/json",
            "pipeline-config-export.json");
    }

    /// <summary>
    /// POST /api/config/import
    /// Accepts a JSON file upload (multipart/form-data, field name "file") containing the config
    /// bundle. Clears ALL existing config before inserting — operation is transactional.
    /// Requires OperatorApiKey policy (042 Req 6.5, Tier 2) — agent-derived keys receive 403.
    /// WARNING: This is destructive — run history and work items are preserved, but all config
    /// (providers, profiles, quality gate configs, reviewer configs, projects, templates, agent routing) is erased.
    /// An older bundle is converted inside the same transaction: the repository-settings move and the agent routing
    /// move (spec 053) run after the bundle is written, so no reader sees the old shapes, and a failed move rolls the
    /// whole import back. It holds <see cref="DatabaseStartupService.MigrationLockKey"/> throughout, so it cannot
    /// interleave with a replica's startup moves.
    /// </summary>
    internal static async Task<IResult> ImportConfigAsync(
        IFormFile file,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConfigurationStore configStore,
        IDistributedLockProvider lockProvider,
        JobTemplateStore jobTemplates,
        CancellationToken ct)
    {
        var toolsByKey = jobTemplates.ToolsByKey();
        var (bundle, error) = await ValidateAndDeserializeBundle(file, toolsByKey, ct);
        if (error is not null) return error;

        // The moves' lock is not reentrant, so the import takes it once and runs the moves without their own.
        await using var lockHandle = await lockProvider.AcquireAsync(DatabaseStartupService.MigrationLockKey, ct);
        AgentRoutingMoveSummary? moveSummary = null;

        // Obtain the execution strategy from a short-lived context that is properly disposed.
        // CreateDbContext() checks out a pooled slot — callers must dispose it to return the slot.
        // The lambda re-creates the DbContext on each retry attempt so the change-tracker
        // starts clean even if a previous attempt left partial state.
        // TODO [WARNING]: If dbFactory.CreateDbContextAsync(ct) itself fails (e.g. pool exhausted or
        // transient error), the ExecuteAsync wrapper is never reached and the caller gets an unhandled
        // exception without benefit of the retry strategy. Because CreateExecutionStrategy() is a pure
        // in-memory call, an alternative is to obtain the strategy from inside the lambda (from the
        // same 'db' context) and remove this outer strategyCtx checkout entirely.
        // See Issue #2576 review findings (Correctness [WARNING], DotNetSpecialist [WARNING]).
        await using var strategyCtx = await dbFactory.CreateDbContextAsync(ct);
        var strategy = strategyCtx.Database.CreateExecutionStrategy();
        // Pass ct as the second argument so the ExecuteAsync overload can forward it to the retry
        // delay between attempts; without it a cancelled request cannot interrupt a retry wait
        // (up to maxRetryDelay:5s × maxRetryCount:3 = 15s of uninterruptible delay).
        await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            await ClearExistingConfig(db, cancellationToken);
            await ApplyBundle(db, bundle, cancellationToken);

            // Older bundles: repository settings move from the connections onto the templates, then label routing
            // becomes agent routing. Both are idempotent, so a bundle of this release passes through unchanged.
            await DatabaseStartupService.ApplyRepositorySettingsMigrationAsync(db, Log.Logger, cancellationToken);
            moveSummary = await AgentRoutingMove.RunAsync(db, toolsByKey, Log.Logger, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }, ct);

        // Invalidate config store caches after the raw DB write (bypasses the store layer).
        // This must run AFTER the ExecuteAsync wrapper so it fires exactly once on commit,
        // not inside the retry lambda where it could fire on a failed attempt before rollback.
        configStore.InvalidateCaches();
        if (moveSummary is not null)
            AgentRoutingMove.LogSummary(Log.Logger, moveSummary);

        return TypedResults.Ok(new ImportExportResult
        {
            Success = true,
            Message = $"Imported: {bundle.ProviderConfigs?.Count ?? 0} providers, " +
                      $"{bundle.AgentProfiles?.Count ?? 0} profiles, " +
                      $"{bundle.QualityGateConfigs?.Count ?? 0} quality gates, " +
                      $"{bundle.ReviewerConfigs?.Count ?? 0} reviewers, " +
                      $"{bundle.Projects?.Count ?? 0} projects, " +
                      $"{bundle.JobTemplates?.Count ?? 0} templates"
        });
    }

    /// <summary>
    /// Deserializes and validates the uploaded config bundle.
    /// Returns a non-null <see cref="ConfigBundle"/> when successful (error is null),
    /// or a non-null <see cref="IResult"/> error when validation fails (bundle is a stub).
    /// </summary>
    // TODO [WARNING]: The return type uses non-nullable ConfigBundle but returns a stub new ConfigBundle()
    // on every error path. The type system does not enforce that callers check 'error' before using
    // 'bundle'. If the null-guard at the call site (if (error is not null) return error) is ever removed
    // or bypassed, the import would proceed with an empty bundle, silently clearing all config and
    // inserting nothing. Consider returning (ConfigBundle? bundle, IResult? error) to force a null-check,
    // or using a discriminated union / exception to express the failure state unambiguously.
    // See Issue #3267 review findings (DotNetSpecialist [WARNING], Correctness [SUGGESTION]).
    private static async Task<(ConfigBundle bundle, IResult? error)> ValidateAndDeserializeBundle(
        IFormFile file,
        IReadOnlyDictionary<string, string> toolsByKey,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return (new ConfigBundle(), TypedResults.BadRequest(new ImportExportResult { Success = false, Message = "No file uploaded" }));

        ConfigBundle? bundle;
        try
        {
            using var stream = file.OpenReadStream();
            bundle = await JsonSerializer.DeserializeAsync<ConfigBundle>(stream, ImportOptions, ct);
        }
        catch (JsonException ex)
        {
            return (new ConfigBundle(), TypedResults.BadRequest(new ImportExportResult { Success = false, Message = $"Invalid JSON: {ex.Message}" }));
        }

        if (bundle is null)
            return (new ConfigBundle(), TypedResults.BadRequest(new ImportExportResult { Success = false, Message = "Empty or invalid bundle" }));

        var settingsErrors = ValidateImportedSettings(bundle);
        if (settingsErrors.Count > 0)
            return (new ConfigBundle(), TypedResults.BadRequest(new ImportExportResult { Success = false, Message = $"Invalid settings: {string.Join(" ", settingsErrors)}" }));

        var routingErrors = ValidateImportedRouting(bundle, toolsByKey);
        if (routingErrors.Count > 0)
            return (new ConfigBundle(), TypedResults.BadRequest(new ImportExportResult { Success = false, Message = $"Invalid agent routing: {string.Join(" ", routingErrors)}" }));

        return (bundle, null);
    }

    /// <summary>
    /// The routing problems in an import bundle (spec 053 req 7.12). Only its shape is checked: task types, models,
    /// efforts when the tool is known, and that each route's profile is in the bundle. A job template this cluster does
    /// not have is not refused, so a bundle from another cluster imports; the Agent Profiles page flags it.
    /// </summary>
    private static IReadOnlyList<string> ValidateImportedRouting(ConfigBundle bundle, IReadOnlyDictionary<string, string> toolsByKey)
    {
        AgentRouting? routing;
        try
        {
            routing = AgentRoutingStore.Deserialize(bundle.AgentRouting);
        }
        catch (JsonException ex)
        {
            return [$"The agent routing cannot be read: {ex.Message}"];
        }
        if (routing is null)
            return [];

        var profiles = new List<AgentProfile>();
        foreach (var dto in bundle.AgentProfiles ?? [])
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dto.Configuration)
                    && JsonSerializer.Deserialize<AgentProfile>(dto.Configuration, PipelineJsonOptions.Lenient) is { } profile)
                    profiles.Add(profile);
            }
            catch (JsonException)
            {
                // A profile that cannot be read is not in the bundle for a route that names it.
            }
        }
        return AgentRouteValidator.ValidateImport(routing, profiles, toolsByKey);
    }

    /// <summary>
    /// Removes all existing config rows (providers, profiles, quality gates, reviewers, projects,
    /// templates, and pipeline config) then flushes the deletes.
    /// Must be called inside the retry execution strategy lambda — <paramref name="ct"/> is the
    /// per-attempt cancellation token, not the outer request token.
    /// </summary>
    private static async Task ClearExistingConfig(PipelineDbContext db, CancellationToken ct)
    {
        // Clear existing config (not runs, consolidation data, or work items — those are preserved).
        // TODO [WARNING]: db.PipelineConfig on a freshly created DbContext returns the change-tracker's
        // local cache, which is empty. RemoveRange(db.PipelineConfig) is therefore a no-op against
        // tracked state. Verify that EF Core translates RemoveRange(DbSet<T>) into a bulk DELETE SQL
        // statement rather than relying on tracked entities — if the delete only fires via tracked
        // state, rows are silently not deleted on retry attempts where the cache starts clean.
        // See Issue #2576 review findings (Correctness [WARNING], DotNetSpecialist [WARNING]).
        db.PipelineConfig.RemoveRange(db.PipelineConfig);
        db.ProviderConfigs.RemoveRange(db.ProviderConfigs);
        db.AgentProfiles.RemoveRange(db.AgentProfiles);
        db.QualityGateConfigs.RemoveRange(db.QualityGateConfigs);
        db.ReviewerConfigs.RemoveRange(db.ReviewerConfigs);
        db.Projects.RemoveRange(db.Projects);
        db.PipelineJobTemplates.RemoveRange(db.PipelineJobTemplates);
        // Of the key-value rows only the routing table is configuration; loop state and the rest stay.
        db.KeyValueStore.RemoveRange(db.KeyValueStore.Where(kv => kv.Key == AgentRouting.StorageKey));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Inserts all entities from <paramref name="bundle"/> into <paramref name="db"/> then flushes
    /// the inserts. Does not commit — the caller owns the transaction boundary.
    /// Must be called inside the retry execution strategy lambda — <paramref name="ct"/> is the
    /// per-attempt cancellation token, not the outer request token.
    /// </summary>
    private static async Task ApplyBundle(PipelineDbContext db, ConfigBundle bundle, CancellationToken ct)
    {
        if (bundle.PipelineConfig is not null)
        {
            db.PipelineConfig.Add(new PipelineConfigEntity
            {
                Id = Guid.NewGuid(),
                Configuration = bundle.PipelineConfig
            });
        }

        foreach (var p in bundle.ProviderConfigs ?? [])
        {
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = p.Id,
                Kind = p.Kind,
                DisplayName = p.DisplayName,
                ProviderType = p.ProviderType,
                Enabled = p.Enabled,
                Configuration = p.Configuration
            });
        }

        foreach (var a in bundle.AgentProfiles ?? [])
        {
            db.AgentProfiles.Add(new AgentProfileEntity
            {
                Id = a.Id,
                Name = a.Name,
                Configuration = a.Configuration
            });
        }

        foreach (var q in bundle.QualityGateConfigs ?? [])
        {
            db.QualityGateConfigs.Add(new QualityGateConfigEntity
            {
                Id = q.Id,
                Name = q.Name,
                Configuration = q.Configuration
            });
        }

        foreach (var r in bundle.ReviewerConfigs ?? [])
        {
            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = r.Id,
                Name = r.Name,
                Configuration = r.Configuration
            });
        }

        foreach (var proj in bundle.Projects ?? [])
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = proj.Id,
                Name = proj.Name,
                Enabled = proj.Enabled,
                Description = proj.Description,
                Settings = proj.Settings
            });
        }

        foreach (var t in bundle.JobTemplates ?? [])
        {
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = t.Id,
                ProjectId = t.ProjectId,
                Name = t.Name,
                Configuration = t.Configuration
            });
        }

        // Validated before anything was cleared (ValidateImportedRouting); stored in this release's shape.
        if (AgentRoutingStore.Deserialize(bundle.AgentRouting) is { } routing)
            db.KeyValueStore.Add(new KeyValueEntity { Key = AgentRouting.StorageKey, Value = AgentRoutingStore.Serialize(routing) });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs a configuration save. The stores refuse an argument they cannot store, such as an ID that is not a GUID, with
    /// an ArgumentException: that is the caller's mistake, so it becomes a 400 with the store's message instead of a 500.
    /// </summary>
    private static async Task<IResult> SaveAsync(Func<Task> save)
    {
        try
        {
            await save();
            return TypedResults.Ok();
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// The settings problems in an import bundle: global settings or project overrides outside their range, or settings
    /// that cannot be read. An import replaces all configuration at once, so any problem rejects the whole bundle.
    /// </summary>
    private static List<string> ValidateImportedSettings(ConfigBundle bundle)
    {
        var errors = new List<string>();
        if (bundle.PipelineConfig is not null)
        {
            try
            {
                var config = JsonSerializer.Deserialize<PipelineConfiguration>(bundle.PipelineConfig, PipelineJsonOptions.Default);
                if (config is not null)
                    errors.AddRange(PipelineSettingsValidator.Validate(config));
            }
            catch (JsonException ex)
            {
                errors.Add($"The pipeline settings cannot be read: {ex.Message}");
            }
        }

        foreach (var project in bundle.Projects ?? [])
        {
            if (project.Settings is null)
                continue;
            try
            {
                var settings = JsonSerializer.Deserialize<PipelineProject>(project.Settings, PipelineJsonOptions.Default);
                if (settings is not null)
                    errors.AddRange(PipelineSettingsValidator.ValidateOverrides(settings).Select(error => $"Project '{project.Name}': {error}"));
            }
            catch (JsonException ex)
            {
                errors.Add($"Project '{project.Name}': its settings cannot be read: {ex.Message}");
            }
        }

        return errors;
    }

    private static async Task<string?> LoadFirstEntityJson<T>(
        Microsoft.EntityFrameworkCore.DbSet<T> dbSet,
        Func<T, string?> selector,
        CancellationToken ct) where T : class
    {
        var entity = await dbSet.AsNoTracking().FirstOrDefaultAsync(ct);
        return entity is null ? null : selector(entity);
    }

    // ── Secret redaction helper (Req 6.4a) ────────────────────────────────

    /// <summary>
    /// Returns a copy of the ProviderConfig with all Settings values replaced by "****".
    /// Key names are preserved; values are always masked.
    /// PUT accepts real values; GET never returns them.
    /// </summary>
    private static ProviderConfig RedactProviderConfig(ProviderConfig config)
    {
        var redactedSettings = config.Settings is { Count: > 0 }
            ? config.Settings.ToDictionary(kv => kv.Key, _ => "****")
            : config.Settings;

        var redactedSecrets = config.Secrets is { Count: > 0 }
            ? config.Secrets.ToDictionary(kv => kv.Key, _ => "****")
            : config.Secrets;

        // ProviderConfig is a class (not a record), so manually construct the redacted copy.
        return new ProviderConfig
        {
            BlacklistedPaths = config.BlacklistedPaths,
            DisplayName = config.DisplayName,
            Id = config.Id,
            Kind = config.Kind,
            ProviderType = config.ProviderType,
            RepositoryRole = config.RepositoryRole,
            RequiredLabels = config.RequiredLabels,
            Secrets = redactedSecrets,
            Settings = redactedSettings,
            SetupSteps = config.SetupSteps,
            SteeringContent = config.SteeringContent
        };
    }
}

/// <summary>
/// Response shape for GET /api/config/projects — project plus its templates.

// ── DTOs for config import/export bundle ──────────────────────────────────────

/// <summary>
/// The full config bundle — serialized to/from JSON for export/import.
/// Same schema as the monolith's ConfigBundle so existing exports remain importable.
/// </summary>
public sealed class ConfigBundle
{
    public string? PipelineConfig { get; set; }
    public List<ProviderConfigDto>? ProviderConfigs { get; set; }
    public List<NamedConfigDto>? AgentProfiles { get; set; }
    public List<NamedConfigDto>? QualityGateConfigs { get; set; }
    public List<NamedConfigDto>? ReviewerConfigs { get; set; }
    public List<ProjectDto>? Projects { get; set; }
    public List<JobTemplateDto>? JobTemplates { get; set; }

    /// <summary>The global routing table as its stored JSON (spec 053); absent in older bundles, which the move converts.</summary>
    public string? AgentRouting { get; set; }
}

/// <summary>Provider config row — Configuration is a serialized ProviderConfig JSON blob.</summary>
public sealed record ProviderConfigDto
{
    public Guid Id { get; init; }
    public ProviderKind Kind { get; init; }
    public string DisplayName { get; init; } = "";
    public string ProviderType { get; init; } = "";
    public bool Enabled { get; init; }
    public string? Configuration { get; init; }
}

/// <summary>Generic named config row (agent profiles, quality gate configs, reviewer configs).</summary>
public sealed class NamedConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

/// <summary>Project row. Its templates are the job templates whose ProjectId points to it; a templateIds list in older bundles is ignored.</summary>
public sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public string? Settings { get; set; }
}

/// <summary>Pipeline job template row.</summary>
public sealed class JobTemplateDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

/// <summary>Response body for POST /api/config/import.</summary>
public sealed class ImportExportResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
}

/// <summary>Request body for POST /api/ui/settings/templates/move.</summary>
public sealed class MoveTemplateFlatRequest
{
    public required string SourceProjectId { get; init; }
    public required string TargetProjectId { get; init; }
    public required string TemplateId { get; init; }
}
