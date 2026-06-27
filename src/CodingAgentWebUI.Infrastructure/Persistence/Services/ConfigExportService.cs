using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Exports the current database state back to JSON file format,
/// producing the same directory structure that <see cref="ConfigMigrationService"/> reads from.
/// Used by the <c>export-config</c> CLI command for rollback scenarios.
/// </summary>
public sealed class ConfigExportService
{
    private static readonly ILogger Logger = Log.ForContext<ConfigExportService>();
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public ConfigExportService(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Exports all configuration and run data from the database to the specified output directory.
    /// Directory structure mirrors <c>config/pipeline/</c>.
    /// </summary>
    public async Task ExportAsync(string outputDir, CancellationToken ct)
    {
        Logger.Information("ConfigExport: Exporting DB state to {OutputDir}", outputDir);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var counts = new ExportCounts();

        // 1. Pipeline config
        await ExportPipelineConfigAsync(db, outputDir, counts, ct);

        // 2. Provider configs
        await ExportProviderConfigsAsync(db, outputDir, counts, ct);

        // 3. Agent profiles
        await ExportAgentProfilesAsync(db, outputDir, counts, ct);

        // 4. Quality gate configs
        await ExportQualityGateConfigsAsync(db, outputDir, counts, ct);

        // 5. Reviewer configs
        await ExportReviewerConfigsAsync(db, outputDir, counts, ct);

        // 6. Projects + templates
        await ExportProjectsAsync(db, outputDir, counts, ct);

        // 7. Consolidation runs
        await ExportConsolidationRunsAsync(db, outputDir, counts, ct);

        // 8. Pipeline runs
        await ExportPipelineRunsAsync(db, outputDir, counts, ct);

        Logger.Information(
            "ConfigExport: Completed. " +
            "PipelineConfig: {PipelineConfig}, ProviderConfigs: {Providers}, " +
            "AgentProfiles: {Profiles}, QualityGates: {QualityGates}, " +
            "Reviewers: {Reviewers}, Projects: {Projects}, " +
            "Templates: {Templates}, ConsolidationRuns: {ConsolidationRuns}, " +
            "PipelineRuns: {PipelineRuns}",
            counts.PipelineConfig, counts.ProviderConfigs,
            counts.AgentProfiles, counts.QualityGates,
            counts.Reviewers, counts.Projects,
            counts.Templates, counts.ConsolidationRuns,
            counts.PipelineRuns);
    }

    private static async Task ExportPipelineConfigAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entity = await db.PipelineConfig.AsNoTracking().FirstOrDefaultAsync(ct);
        if (entity?.Configuration is null)
            return;

        var json = FormatJsonDocument(entity.Configuration);
        var path = Path.Combine(outputDir, "pipeline-config.json");
        await File.WriteAllTextAsync(path, json, ct);
        counts.PipelineConfig = 1;
    }

    private static async Task ExportProviderConfigsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.ProviderConfigs.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        foreach (var entity in entities)
        {
            if (entity.Configuration is null)
                continue;

            var kindDir = entity.Kind switch
            {
                ProviderKind.Issue => "issue",
                ProviderKind.Repository => "repository",
                ProviderKind.Agent => "agent",
                ProviderKind.Brain => "brain",
                ProviderKind.Pipeline => "pipeline",
                _ => null
            };

            if (kindDir is null)
                continue;

            var dir = Path.Combine(outputDir, "providers", kindDir);
            Directory.CreateDirectory(dir);

            var json = FormatJsonDocument(entity.Configuration);
            var filePath = Path.Combine(dir, $"{entity.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.ProviderConfigs++;
        }
    }

    private static async Task ExportAgentProfilesAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.AgentProfiles.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        var dir = Path.Combine(outputDir, "profiles");
        Directory.CreateDirectory(dir);

        foreach (var entity in entities)
        {
            if (entity.Configuration is null)
                continue;

            var json = FormatJsonDocument(entity.Configuration);
            var filePath = Path.Combine(dir, $"{entity.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.AgentProfiles++;
        }
    }

    private static async Task ExportQualityGateConfigsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.QualityGateConfigs.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        var dir = Path.Combine(outputDir, "quality-gates");
        Directory.CreateDirectory(dir);

        foreach (var entity in entities)
        {
            if (entity.Configuration is null)
                continue;

            var json = FormatJsonDocument(entity.Configuration);
            var filePath = Path.Combine(dir, $"{entity.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.QualityGates++;
        }
    }

    private static async Task ExportReviewerConfigsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.ReviewerConfigs.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        var dir = Path.Combine(outputDir, "reviewers");
        Directory.CreateDirectory(dir);

        foreach (var entity in entities)
        {
            if (entity.Configuration is null)
                continue;

            var json = FormatJsonDocument(entity.Configuration);
            var filePath = Path.Combine(dir, $"{entity.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.Reviewers++;
        }
    }

    private static async Task ExportProjectsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
        if (projects.Count == 0)
            return;

        var projectsDir = Path.Combine(outputDir, "projects");
        Directory.CreateDirectory(projectsDir);

        foreach (var project in projects)
        {
            if (project.Settings is null)
                continue;

            var json = FormatJsonDocument(project.Settings);
            var filePath = Path.Combine(projectsDir, $"{project.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.Projects++;

            // Export templates for this project
            var templates = await db.PipelineJobTemplates
                .AsNoTracking()
                .Where(t => t.ProjectId == project.Id)
                .ToListAsync(ct);

            if (templates.Count > 0)
            {
                var templatesDir = Path.Combine(projectsDir, project.Id.ToString(), "templates");
                Directory.CreateDirectory(templatesDir);

                foreach (var template in templates)
                {
                    if (template.Configuration is null)
                        continue;

                    var templateJson = FormatJsonDocument(template.Configuration);
                    var templatePath = Path.Combine(templatesDir, $"{template.Id}.json");
                    await File.WriteAllTextAsync(templatePath, templateJson, ct);
                    counts.Templates++;
                }
            }
        }
    }

    private static async Task ExportConsolidationRunsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.ConsolidationRuns.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        var dir = Path.Combine(outputDir, "consolidation-runs");
        Directory.CreateDirectory(dir);

        foreach (var entity in entities)
        {
            if (entity.Data is null)
                continue;

            var json = FormatJsonDocument(entity.Data);
            var filePath = Path.Combine(dir, $"{entity.Id}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.ConsolidationRuns++;
        }
    }

    private static async Task ExportPipelineRunsAsync(
        PipelineDbContext db, string outputDir, ExportCounts counts, CancellationToken ct)
    {
        var entities = await db.PipelineRuns.AsNoTracking().ToListAsync(ct);
        if (entities.Count == 0)
            return;

        var dir = Path.Combine(outputDir, "runs");
        Directory.CreateDirectory(dir);

        foreach (var entity in entities)
        {
            var summary = MapToRunSummary(entity);
            var json = JsonSerializer.Serialize(summary, JsonOptions);
            var filePath = Path.Combine(dir, $"{entity.RunId}.json");
            await File.WriteAllTextAsync(filePath, json, ct);
            counts.PipelineRuns++;
        }
    }

    private static PipelineRunSummary MapToRunSummary(PipelineRunEntity entity) => new()
    {
        RunId = entity.RunId.ToString(),
        IssueIdentifier = entity.IssueIdentifier,
        IssueTitle = entity.IssueTitle ?? "",
        FinalStep = entity.FinalStep,
        StartedAtOffset = entity.StartedAt,
        CompletedAtOffset = entity.CompletedAt,
#pragma warning disable CS0618 // Legacy fields populated for backward compat
        StartedAt = entity.StartedAt.UtcDateTime,
        CompletedAt = entity.CompletedAt?.UtcDateTime,
#pragma warning restore CS0618
        RetryCount = entity.RetryCount,
        PullRequestUrl = entity.PullRequestUrl,
        ModelName = entity.ModelName,
        AgentId = entity.AgentId,
        ProjectName = entity.ProjectName,
        RunType = entity.RunType
    };

    /// <summary>
    /// Re-serializes a JsonDocument with indented formatting via PipelineJsonOptions.
    /// This ensures exported JSON uses the same formatting as the original files.
    /// </summary>
    private static string FormatJsonDocument(JsonDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            doc.RootElement.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class ExportCounts
    {
        public int PipelineConfig;
        public int ProviderConfigs;
        public int AgentProfiles;
        public int QualityGates;
        public int Reviewers;
        public int Projects;
        public int Templates;
        public int ConsolidationRuns;
        public int PipelineRuns;
    }
}
