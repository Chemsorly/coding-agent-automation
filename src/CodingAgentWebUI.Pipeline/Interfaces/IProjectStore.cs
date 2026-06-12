using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// CRUD interface for project persistence. Includes GetProjectByIdAsync (unlike other stores
/// which only have Load/Save/Delete) because project lookup by ID is frequent at dispatch time
/// — the dispatcher resolves a template's parent project for settings resolution. Loading all
/// projects and filtering client-side would be wasteful on every dispatch call.
/// </summary>
public interface IProjectStore
{
    Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct);
    Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct);
    Task SaveProjectAsync(PipelineProject project, CancellationToken ct);
    Task DeleteProjectAsync(string id, CancellationToken ct);

    // ── Template CRUD ───────────────────────────────────────────────────

    /// <summary>Load templates for a project, ordered by TemplateIds position.</summary>
    Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct);

    /// <summary>Load all templates across all projects.</summary>
    Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct);

    /// <summary>Save a template under a project. Adds the template ID to TemplateIds if not present.</summary>
    Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct);

    /// <summary>Delete a template. Removes the template ID from the project's TemplateIds.</summary>
    Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct);

    /// <summary>Move a template from one project to another. Updates TemplateIds on both projects.</summary>
    Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, string templateId, CancellationToken ct);
}
