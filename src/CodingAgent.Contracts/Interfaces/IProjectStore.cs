using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// CRUD interface for project persistence. Includes GetProjectByIdAsync (unlike other stores
/// which only have Load/Save/Delete) because project lookup by ID is frequent at dispatch time
/// — the dispatcher resolves a template's parent project for settings resolution. Loading all
/// projects and filtering client-side would be wasteful on every dispatch call.
/// </summary>
public interface IProjectStore
{
    Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct);
    Task<PipelineProject?> GetProjectByIdAsync(ProjectId id, CancellationToken ct);
    Task SaveProjectAsync(PipelineProject project, CancellationToken ct);
    Task DeleteProjectAsync(ProjectId id, CancellationToken ct);

    // ── Template CRUD ───────────────────────────────────────────────────

    /// <summary>Load templates for a project, in <see cref="TemplateOrder"/> (by name).</summary>
    Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(ProjectId projectId, CancellationToken ct);

    /// <summary>Load all templates across all projects.</summary>
    Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct);

    /// <summary>Save a template under a project. The template's own project is the only membership record, so this also moves it.</summary>
    Task SaveTemplateAsync(ProjectId projectId, PipelineJobTemplate template, CancellationToken ct);

    /// <summary>Delete a template, which removes it from its project.</summary>
    Task DeleteTemplateAsync(ProjectId projectId, TemplateId templateId, CancellationToken ct);

    /// <summary>Move a template to another project by changing the template's own project. No-op when the target project does not exist.</summary>
    Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, TemplateId templateId, CancellationToken ct);

    /// <summary>
    /// Returns <c>true</c> if at least one <see cref="PipelineJobTemplate"/> with <c>Enabled = true</c>
    /// exists across all projects. Used by the first-run banner to avoid loading all templates.
    /// </summary>
    Task<bool> HasEnabledTemplatesAsync(CancellationToken ct);
}
