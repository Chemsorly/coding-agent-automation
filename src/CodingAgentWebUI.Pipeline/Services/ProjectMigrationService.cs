using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// One-time startup migration that ensures projects exist and creates a Default project
/// referencing all existing template IDs when no projects have been created yet.
/// Templates remain in PipelineConfiguration.PipelineJobTemplates as the data store (Phase 1).
/// The project only stores ownership IDs — the legacy list is NOT cleared.
/// </summary>
// TODO: Remove migration code after one release cycle (can be cleaned up during refactoring session)
public static class ProjectMigrationService
{
    /// <summary>
    /// One-time migration: creates Default project referencing existing template IDs.
    /// Templates REMAIN in PipelineConfiguration.PipelineJobTemplates (Phase 1 — templates
    /// are still stored/loaded from the global config). The project only stores ownership IDs.
    /// The legacy list is NOT cleared — it continues to serve as the template data store.
    /// Idempotent — no-op if projects already exist.
    /// </summary>
    public static async Task MigrateToProjectsAsync(
        IProjectStore projectStore,
        IPipelineConfigStore configStore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(configStore);

        var existingProjects = await projectStore.LoadProjectsAsync(ct);
        if (existingProjects.Count > 0)
        {
            // Already migrated — ensure Default project self-healing
            await EnsureDefaultProjectExistsAsync(existingProjects, projectStore, ct);
            return;
        }

        var config = await configStore.LoadPipelineConfigAsync(ct);
        if (config.PipelineJobTemplates.Count == 0)
        {
            // No templates to migrate — just create empty Default project
            await projectStore.SaveProjectAsync(new PipelineProject
            {
                Id = WellKnownIds.DefaultProjectId,
                Name = "Default",
                TemplateIds = []
            }, ct);
            return;
        }

        // Create Default project referencing all existing template IDs.
        // NOTE: Templates remain in PipelineConfiguration.PipelineJobTemplates — the project
        // only stores IDs. The global config list is NOT cleared in Phase 1 to preserve
        // backward compatibility with ApplyTemplateOverrides, ConsolidationDispatcher, and
        // rollback safety. A Phase 2 TODO comment on the property signals intent for future removal.
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = config.PipelineJobTemplates.Select(t => t.Id).ToList()
        };

        await projectStore.SaveProjectAsync(defaultProject, ct);

        Log.Information("Migrated {Count} templates to Default project {ProjectId}",
            defaultProject.TemplateIds.Count, defaultProject.Id);
    }

    /// <summary>
    /// Self-healing guard: if the Default project file was corrupted or deleted,
    /// recreate it unconditionally. Called every startup after LoadProjectsAsync.
    /// </summary>
    public static async Task EnsureDefaultProjectExistsAsync(
        IReadOnlyList<PipelineProject> projects,
        IProjectStore projectStore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(projectStore);

        if (projects.Any(p => p.Id == WellKnownIds.DefaultProjectId))
            return;

        Log.Warning("Default project not found after load — recreating (file may have been corrupted or deleted)");
        await projectStore.SaveProjectAsync(new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = []
        }, ct);
    }
}
