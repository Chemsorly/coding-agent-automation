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
}
