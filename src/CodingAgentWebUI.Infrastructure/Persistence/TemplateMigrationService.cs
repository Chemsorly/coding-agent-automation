#pragma warning disable CS0618 // PipelineJobTemplates is [Obsolete] — this service performs the migration away from it

using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Migrates templates from the global PipelineJobTemplates list to per-project directories.
/// Idempotent: safe to run on every startup.
/// </summary>
public static class TemplateMigrationService
{
    private static readonly ILogger Logger = Log.ForContext(typeof(TemplateMigrationService));

    public static async Task MigrateAsync(
        IConfigurationStore store,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        if (config.PipelineJobTemplates.Count == 0)
            return;

        var projects = await store.LoadProjectsAsync(ct);
        var projectByTemplateId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var templateId in project.TemplateIds)
                projectByTemplateId.TryAdd(templateId, project.Id);
        }

        // Ensure Default project exists for orphan assignment
        string? defaultProjectId = null;
        var orphans = config.PipelineJobTemplates
            .Where(t => !projectByTemplateId.ContainsKey(t.Id))
            .ToList();

        if (orphans.Count > 0)
        {
            var defaultProject = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, ct);
            if (defaultProject is null)
            {
                defaultProject = new PipelineProject
                {
                    Id = WellKnownIds.DefaultProjectId,
                    Name = "Default",
                    TemplateIds = []
                };
                await store.SaveProjectAsync(defaultProject, ct);
                Logger.Information("Created Default project for orphan template migration");

                // Re-read to confirm persistence succeeded
                defaultProject = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, ct);
                if (defaultProject is null)
                {
                    Logger.Warning("Failed to persist Default project — orphan templates will not be migrated");
                }
            }

            defaultProjectId = defaultProject?.Id;
        }

        foreach (var template in config.PipelineJobTemplates)
        {
            try
            {
                if (projectByTemplateId.TryGetValue(template.Id, out var projectId))
                {
                    await store.SaveTemplateAsync(projectId, template, ct);
                }
                else if (defaultProjectId is not null)
                {
                    await store.SaveTemplateAsync(defaultProjectId, template, ct);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to migrate template '{TemplateId}', continuing with remaining templates", template.Id);
            }
        }

        Logger.Information("Template migration completed: {Total} templates processed, {Orphans} assigned to Default project",
            config.PipelineJobTemplates.Count, orphans.Count);
    }
}
