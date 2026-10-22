using System.Text.Json;
using System.Text.Json.Nodes;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure;

/// <summary>What one run of <see cref="AgentRoutingMove"/> did (spec 053 req 7.11). Holds no secret values.</summary>
/// <param name="FullMove">
/// True when today's routes were written down. False when the routing table already existed and only legacy profiles,
/// agent provider configs or default labels written since (by a previous-release replica) were converted.
/// </param>
public sealed record AgentRoutingMoveSummary(
    bool FullMove,
    int ProfilesMoved,
    int ProfilesDisabled,
    IReadOnlyList<string> DuplicateNames,
    string DefaultRoute,
    int GlobalTaskRows,
    int TemplatesWithRows,
    IReadOnlyList<string> TemplatesWithoutProfile,
    IReadOnlyList<string> TemplatesRelabelled,
    int ProviderConfigsDeleted);

/// <summary>
/// Spec 053 req 7.1–7.11: moves the label-routing configuration to agent routing, once, so every work item routes as
/// before on the first day. It computes today's route for every template × task type (<see cref="LegacyRouting"/>),
/// writes the most common implementation route down as the global default and the other cells as repository rows,
/// converts profiles (tool settings from their agent provider config, the job template key, no labels), gives each
/// template the labels of the profile that picked its quality gates and reviewers, then deletes the agent provider
/// configs and the default labels.
/// <para>
/// It runs only when legacy data exists (DD9, by value: a profile with a non-empty <c>agentProviderConfigId</c> or
/// <c>matchLabels</c>, a kind-Agent provider config, non-empty <c>defaultRequiredAgentLabels</c>). When the routing
/// table already exists, the routes are not computed again: only the legacy data is converted, and a converted profile
/// whose agent provider config is gone is disabled. Without legacy data it only adds an empty routing table when none
/// exists. Idempotent.
/// </para>
/// </summary>
public static class AgentRoutingMove
{
    private static readonly string[] ToolLabels =
        [JobTemplateProviderType.Kiro, JobTemplateProviderType.Opencode, JobTemplateProviderType.Claude];

    private static readonly HashSet<string> KnownToolSettings = new(StringComparer.Ordinal)
    {
        ProviderSettingKeys.AgentName, ProviderSettingKeys.BaseUrl, ProviderSettingKeys.AuthMode, ProviderSettingKeys.McpConfigPath,
    };

    /// <summary>
    /// Takes <c>MigrationLockKey</c>, then runs <see cref="RunAsync"/> in one transaction inside the EF execution
    /// strategy, and logs the summary line. The startup entry point. The lock is not reentrant: a caller that holds it
    /// already (a config import) calls <see cref="RunAsync"/> inside its own transaction instead.
    /// </summary>
    /// <returns>The summary, or null when there was nothing to move.</returns>
    public static async Task<AgentRoutingMoveSummary?> RunWithLockAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider lockProvider,
        IReadOnlyDictionary<string, string> toolsByKey,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(lockProvider);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        ArgumentNullException.ThrowIfNull(logger);

        await using var lockHandle = await lockProvider.AcquireAsync(DatabaseStartupService.MigrationLockKey, ct);
        await using var strategyDb = await dbFactory.CreateDbContextAsync(ct);
        var strategy = strategyDb.Database.CreateExecutionStrategy();

        var summary = await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var moved = await RunAsync(db, toolsByKey, logger, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return moved;
        }, ct);

        if (summary is not null)
            LogSummary(logger, summary);
        return summary;
    }

    /// <summary>
    /// Runs the move on <paramref name="db"/> and saves it, inside the caller's transaction. Reads everything it needs
    /// itself, so a retried transaction starts clean. Does not log the summary line (<see cref="LogSummary"/>).
    /// </summary>
    /// <param name="toolsByKey">Job template key → <c>providerType</c> (<c>JobTemplateStore.ToolsByKey()</c>).</param>
    /// <returns>The summary, or null when there was nothing to move.</returns>
    public static async Task<AgentRoutingMoveSummary?> RunAsync(
        PipelineDbContext db,
        IReadOnlyDictionary<string, string> toolsByKey,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        ArgumentNullException.ThrowIfNull(logger);

        var state = await LoadAsync(db, ct);
        var legacyProfiles = state.Profiles.Where(p => p.Profile.IsLegacy).ToList();
        var hasLegacyData = legacyProfiles.Count > 0
            || state.AgentConfigEntities.Count > 0
            || !string.IsNullOrWhiteSpace(state.DefaultRequiredAgentLabels);

        if (!hasLegacyData)
        {
            if (!state.HasRouting)
            {
                await AgentRoutingStore.StageAsync(db, AgentRouting.Empty, ct);
                await db.SaveChangesAsync(ct);
            }
            return null;
        }

        var fullMove = !state.HasRouting;
        var defaultRoute = "none";
        var templatesWithRows = 0;
        var templatesWithoutProfile = new List<string>();
        var templatesRelabelled = new List<string>();

        if (fullMove)
        {
            // Steps 1–3 and 6 read the profiles before step 4 converts them.
            var today = ComputeTodaysRoutes(state, toolsByKey, logger);
            var defaultCell = PickDefault(state, today);
            defaultRoute = defaultCell is null ? "none" : Describe(defaultCell.Value);

            foreach (var template in state.Templates)
            {
                var rows = WriteRows(template, today, defaultCell, logger, templatesWithoutProfile);
                if (rows.Count > 0)
                    templatesWithRows++;
                var labels = NewStackLabels(template, today, state, logger, templatesRelabelled);
                template.Entity.Configuration = Serialize(template.Template with
                {
                    AgentRoutes = rows.Count > 0 ? rows : null,
                    RequiredLabels = labels,
                });
            }

            await AgentRoutingStore.StageAsync(db, new AgentRouting { Default = defaultCell?.Route }, ct);
        }

        var (moved, disabled) = ConvertProfiles(legacyProfiles, state, toolsByKey, logger);
        var duplicateNames = legacyProfiles
            .GroupBy(p => p.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // Step 7: the agent provider configs and the default labels go.
        db.ProviderConfigs.RemoveRange(state.AgentConfigEntities);
        RemoveDefaultRequiredAgentLabels(state);

        await db.SaveChangesAsync(ct);

        return new AgentRoutingMoveSummary(
            fullMove, moved, disabled, duplicateNames, defaultRoute, GlobalTaskRows: 0, templatesWithRows,
            templatesWithoutProfile, templatesRelabelled, state.AgentConfigEntities.Count);
    }

    /// <summary>The summary line (req 7.11), with the counts as properties (req 9.13).</summary>
    public static void LogSummary(Serilog.ILogger logger, AgentRoutingMoveSummary summary)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(summary);
        logger.Information(
            "Agent routing move ({MoveKind}): {ProfilesMoved} agent profiles moved, {ProfilesDisabled} disabled, " +
            "duplicate names [{DuplicateNames}]; default route {DefaultRoute}; {GlobalTaskRows} global task rows; " +
            "{TemplatesWithRows} templates with their own rows; templates that had no agent profile [{TemplatesWithoutProfile}]; " +
            "templates whose stack labels changed [{TemplatesRelabelled}]; {ProviderConfigsDeleted} agent provider configs deleted",
            summary.FullMove ? "full" : "legacy data written since the move",
            summary.ProfilesMoved, summary.ProfilesDisabled, string.Join(", ", summary.DuplicateNames),
            summary.DefaultRoute, summary.GlobalTaskRows, summary.TemplatesWithRows,
            string.Join(", ", summary.TemplatesWithoutProfile), string.Join(", ", summary.TemplatesRelabelled),
            summary.ProviderConfigsDeleted);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    private sealed record ProfileRow(AgentProfileEntity Entity, LegacyProfile Profile);

    private sealed record TemplateRow(PipelineJobTemplateEntity Entity, PipelineJobTemplate Template);

    private sealed record MoveState(
        IReadOnlyList<ProfileRow> Profiles,
        IReadOnlyList<ProviderConfigEntity> AgentConfigEntities,
        IReadOnlyDictionary<string, ProviderConfig> AgentConfigs,
        IReadOnlyList<TemplateRow> Templates,
        PipelineConfigEntity? PipelineEntity,
        string? DefaultRequiredAgentLabels,
        IReadOnlySet<string> NamedLabels,
        bool HasRouting);

    private static async Task<MoveState> LoadAsync(PipelineDbContext db, CancellationToken ct)
    {
        var profiles = (await db.AgentProfiles.ToListAsync(ct))
            .Select(e => LegacyProfileJson.Parse(e.Configuration) is { } p ? new ProfileRow(e, p) : null)
            .OfType<ProfileRow>()
            .ToList();

        var agentConfigEntities = await db.ProviderConfigs.Where(p => p.Kind == ProviderKind.Agent).ToListAsync(ct);
        var agentConfigs = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal);
        foreach (var entity in agentConfigEntities)
        {
            if (Deserialize<ProviderConfig>(entity.Configuration) is { } config)
                agentConfigs[config.Id] = config;
        }

        var templates = (await db.PipelineJobTemplates.ToListAsync(ct))
            .Select(e => Deserialize<PipelineJobTemplate>(e.Configuration) is { } t ? new TemplateRow(e, t) : null)
            .OfType<TemplateRow>()
            .OrderBy(t => t.Template.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Template.Id, StringComparer.Ordinal)
            .ToList();

        var pipelineEntity = await db.PipelineConfig.FirstOrDefaultAsync(ct);
        var pipelineNode = ParseObject(pipelineEntity?.Configuration);
        var defaultLabels = pipelineNode is null ? null : Get(pipelineNode, "defaultRequiredAgentLabels")?.GetValue<string>();

        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in await db.QualityGateConfigs.AsNoTracking().ToListAsync(ct))
            named.UnionWith(Deserialize<QualityGateConfiguration>(entity.Configuration)?.MatchLabels ?? []);
        foreach (var entity in await db.ReviewerConfigs.AsNoTracking().ToListAsync(ct))
            named.UnionWith(Deserialize<ReviewerConfiguration>(entity.Configuration)?.MatchLabels ?? []);

        var hasRouting = await db.KeyValueStore.AnyAsync(kv => kv.Key == AgentRouting.StorageKey, ct);

        return new MoveState(profiles, agentConfigEntities, agentConfigs, templates, pipelineEntity, defaultLabels, named, hasRouting);
    }

    // ── Steps 1–3: today's routes, the default, the repository rows ──────────

    /// <summary>A profile today's code picks for a cell, with its route.</summary>
    private readonly record struct Cell(LegacyProfile Profile, AgentRoute Route);

    private static Dictionary<(string TemplateId, WorkItemTaskType TaskType), Cell?> ComputeTodaysRoutes(
        MoveState state, IReadOnlyDictionary<string, string> toolsByKey, Serilog.ILogger logger)
    {
        var profiles = state.Profiles.Select(p => p.Profile).ToList();
        var dispatchable = toolsByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var today = new Dictionary<(string, WorkItemTaskType), Cell?>();

        foreach (var template in state.Templates.Select(row => row.Template))
        {
            var dispatch = LegacyRouting.DispatchProfile(template, state.DefaultRequiredAgentLabels, profiles);
            foreach (var taskType in new[] { WorkItemTaskType.Implementation, WorkItemTaskType.Review, WorkItemTaskType.Decomposition, WorkItemTaskType.Triage })
                today[(template.Id, taskType)] = ToCell(dispatch, state, toolsByKey);

            // Operator triage pins no profile; its claim re-resolves from the selector. The move keeps the
            // dispatch-time profile (req 7.3).
            if (dispatch is not null
                && LegacyRouting.ClaimProfileForSelector(dispatch.JobTemplateKey(), profiles) is var claim
                && claim?.Id != dispatch.Id)
            {
                logger.Warning(
                    "Agent routing move: operator triage on template {TemplateName} picked agent profile {AgentProfile} " +
                    "at dispatch and {ClaimProfile} at claim; the Triage route keeps the dispatch-time profile",
                    template.Name, dispatch.DisplayName, claim?.DisplayName ?? "none");
            }

            var consolidation = LegacyRouting.ConsolidationProfile(template, state.DefaultRequiredAgentLabels, profiles, dispatchable);
            var unfiltered = LegacyRouting.ConsolidationProfile(template, state.DefaultRequiredAgentLabels, profiles, null);
            if (unfiltered?.Id != consolidation?.Id)
            {
                logger.Warning(
                    "Agent routing move: automatic consolidation of template {TemplateName} ran on agent profile {UnfilteredProfile} " +
                    "(the Scheduler did not filter by job template); the Consolidation route uses {AgentProfile}, as the Consolidation page did",
                    template.Name, unfiltered?.DisplayName ?? "none", consolidation?.DisplayName ?? "none");
            }
            today[(template.Id, WorkItemTaskType.Consolidation)] = ToCell(consolidation, state, toolsByKey);
        }
        return today;
    }

    private static Cell? ToCell(LegacyProfile? profile, MoveState state, IReadOnlyDictionary<string, string> toolsByKey)
    {
        if (profile is null)
            return null;
        IReadOnlyDictionary<string, string> settings = state.AgentConfigs.TryGetValue(profile.AgentProviderConfigId, out var config)
            ? config.Settings
            : new Dictionary<string, string>();
        var tool = toolsByKey.TryGetValue(profile.JobTemplateKey(), out var providerType) ? AgentToolCatalog.Find(providerType) : null;
        return new Cell(profile, new AgentRoute(profile.Id, Model(settings), EffortRun(tool, Effort(settings))));
    }

    /// <summary>
    /// Req 7.3: the effort the tool ran at, so the route keeps today's behaviour and the Routing page can save it. kiro-cli
    /// runs <c>xhigh</c> at <c>high</c> (<c>KiroCliAgentProvider.ToKiroEffort</c>), and a tool without efforts ignored it.
    /// A profile whose job template has no tool keeps its effort; the move disables it.
    /// </summary>
    private static string EffortRun(AgentTool? tool, string effort)
    {
        if (tool is null || AgentToolCatalog.AcceptsEffort(tool, effort))
            return effort;
        if (effort == "xhigh" && AgentToolCatalog.AcceptsEffort(tool, "high"))
            return "high";
        return AgentToolCatalog.Auto;
    }

    /// <summary>Req 7.7: a blank model is <c>auto</c>, as the providers treat it.</summary>
    private static string Model(IReadOnlyDictionary<string, string> settings) =>
        settings.GetValueOrDefault(ProviderSettingKeys.Model) is { } model && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : AgentToolCatalog.Auto;

    /// <summary>Req 7.7: a blank or unknown effort is <c>auto</c>, as <see cref="AgentEffortLevelExtensions.ParseEffort"/> reads it.</summary>
    private static string Effort(IReadOnlyDictionary<string, string> settings) =>
        AgentEffortLevelExtensions.ParseEffort(settings.GetValueOrDefault(ProviderSettingKeys.Effort)).ToCliValue()
            ?? AgentToolCatalog.Auto;

    /// <summary>
    /// Req 7.4: the route most enabled templates' Implementation cells have; ties go to the lowest profile Id.
    /// </summary>
    private static Cell? PickDefault(MoveState state, Dictionary<(string, WorkItemTaskType), Cell?> today) =>
        state.Templates
            .Where(t => t.Template.Enabled)
            .Select(t => today[(t.Template.Id, WorkItemTaskType.Implementation)])
            .OfType<Cell>()
            .GroupBy(c => c.Route)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ProfileId, StringComparer.Ordinal)
            .Select(g => (Cell?)g.First())
            .FirstOrDefault();

    /// <summary>
    /// Req 7.5–7.6: a repository row for every cell whose route differs from the default. A template with no profile
    /// in any cell gets no rows, and from now on uses the global routes; that is logged.
    /// </summary>
    private static Dictionary<WorkItemTaskType, AgentRoute> WriteRows(
        TemplateRow row,
        Dictionary<(string, WorkItemTaskType), Cell?> today,
        Cell? defaultCell,
        Serilog.ILogger logger,
        List<string> templatesWithoutProfile)
    {
        var template = row.Template;
        var rows = new Dictionary<WorkItemTaskType, AgentRoute>();
        var unrouted = new List<WorkItemTaskType>();
        foreach (var taskType in Enum.GetValues<WorkItemTaskType>())
        {
            if (today[(template.Id, taskType)] is not { } cell)
                unrouted.Add(taskType);
            else if (cell.Route != defaultCell?.Route)
                rows[taskType] = cell.Route;
        }

        var nowUses = defaultCell is { } d ? Describe(d) : "no route, so its work is refused";
        if (unrouted.Count == Enum.GetValues<WorkItemTaskType>().Length)
        {
            templatesWithoutProfile.Add(template.Name);
            logger.Warning("Template {TemplateName} had no agent profile; it now uses {Route}", template.Name, nowUses);
        }
        else if (unrouted.Count > 0)
        {
            logger.Warning(
                "Template {TemplateName} had no agent profile for {TaskTypes}; it now uses {Route} for them",
                template.Name, string.Join(", ", unrouted.Select(AgentRouteLabels.TaskType)), nowUses);
        }
        return rows;
    }

    // ── Step 4: profiles ─────────────────────────────────────────────────────

    private static (int Moved, int Disabled) ConvertProfiles(
        IReadOnlyList<ProfileRow> legacyProfiles,
        MoveState state,
        IReadOnlyDictionary<string, string> toolsByKey,
        Serilog.ILogger logger)
    {
        var disabled = 0;
        foreach (var (entity, profile) in legacyProfiles)
        {
            var config = state.AgentConfigs.GetValueOrDefault(profile.AgentProviderConfigId);
            var settings = (config?.Settings ?? new Dictionary<string, string>())
                .Where(s => s.Key is not ProviderSettingKeys.Model and not ProviderSettingKeys.Effort)
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
            var unknown = settings.Keys.Where(k => !KnownToolSettings.Contains(k)).ToList();
            if (unknown.Count > 0)
            {
                logger.Information(
                    "Agent routing move: agent profile {AgentProfile} keeps the tool settings [{SettingKeys}], which the form does not show",
                    profile.DisplayName, string.Join(", ", unknown));
            }

            var key = profile.JobTemplateKey();
            var reason = DisableReason(config is not null, key, toolsByKey);
            var enabled = profile.Enabled && reason is null;
            if (profile.Enabled && reason is not null)
            {
                disabled++;
                logger.Warning(
                    "Agent routing move: agent profile {AgentProfile} ({AgentProfileId}) is moved disabled: {Reason}",
                    profile.DisplayName, profile.Id, reason);
            }

            entity.Configuration = LegacyProfileJson.Convert(entity.Configuration!, key, settings, enabled);
        }
        return (legacyProfiles.Count, disabled);
    }

    /// <summary>Why a profile is moved disabled, or null when it can stay enabled.</summary>
    private static string? DisableReason(bool hasAgentConfig, string jobTemplateKey, IReadOnlyDictionary<string, string> toolsByKey)
    {
        if (!hasAgentConfig)
            return "its agent provider config does not exist";
        if (string.IsNullOrEmpty(jobTemplateKey))
            return "it names no job template";
        if (toolsByKey.Count > 0 && toolsByKey.TryGetValue(jobTemplateKey, out var tool) && AgentToolCatalog.Find(tool) is null)
            return $"its job template {jobTemplateKey} has no providerType";
        return null;
    }

    // ── Step 6: stack labels ─────────────────────────────────────────────────

    /// <summary>
    /// Req 7.8: the labels of the profile the template's Implementation cell resolves to, because they picked its
    /// quality gates and reviewers until now; a template without a profile keeps its labels, or gets the default
    /// labels. Tool labels that no quality gate or reviewer config names are dropped; under the intersection rule
    /// that changes no match.
    /// </summary>
    private static IReadOnlyList<string>? NewStackLabels(
        TemplateRow row,
        Dictionary<(string, WorkItemTaskType), Cell?> today,
        MoveState state,
        Serilog.ILogger logger,
        List<string> templatesRelabelled)
    {
        var template = row.Template;
        IReadOnlyList<string> source = today[(template.Id, WorkItemTaskType.Implementation)] is { } cell
            ? cell.Profile.MatchLabels
            : LegacyRouting.RequiredLabels(template, state.DefaultRequiredAgentLabels);
        var labels = source
            .Where(l => !ToolLabels.Contains(l, StringComparer.OrdinalIgnoreCase) || state.NamedLabels.Contains(l))
            .ToList();

        var old = template.RequiredLabels ?? [];
        if (old.ToHashSet(StringComparer.Ordinal).SetEquals(labels))
            return template.RequiredLabels;

        templatesRelabelled.Add(template.Name);
        logger.Information(
            "Agent routing move: stack labels of template {TemplateName} changed from [{OldLabels}] to [{NewLabels}]",
            template.Name, string.Join(", ", old), string.Join(", ", labels));
        return labels.Count > 0 ? labels : null;
    }

    // ── Step 7 helpers ───────────────────────────────────────────────────────

    private static void RemoveDefaultRequiredAgentLabels(MoveState state)
    {
        if (state.PipelineEntity is not { } entity || ParseObject(entity.Configuration) is not { } node)
            return;
        var keys = node.Select(p => p.Key)
            .Where(k => string.Equals(k, "defaultRequiredAgentLabels", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keys.Count == 0)
            return;
        foreach (var key in keys)
            node.Remove(key);
        entity.Configuration = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    private static string Describe(Cell cell) =>
        $"{cell.Profile.DisplayName} · {cell.Route.Model} · {cell.Route.Effort}";

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, PipelineJsonOptions.Lenient);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, PipelineJsonOptions.Default);

    private static JsonObject? ParseObject(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;

    private static JsonNode? Get(JsonObject node, string name) =>
        node.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
