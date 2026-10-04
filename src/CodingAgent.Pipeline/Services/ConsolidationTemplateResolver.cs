using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Resolves pipeline job templates from projects for consolidation runs.
/// </summary>
public sealed class ConsolidationTemplateResolver
{
    private readonly IProjectStore _projectStore;

    public ConsolidationTemplateResolver(IProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        _projectStore = projectStore;
    }

    /// <summary>
    /// Resolves a template by ID from projects via IProjectStore.
    /// </summary>
    public async Task<PipelineJobTemplate?> ResolveTemplateAsync(TemplateId templateId, CancellationToken ct)
    {
        var (template, _, _) = await ResolveTemplateWithProjectAsync(templateId, ct);
        return template;
    }

    /// <summary>
    /// Resolves a template by ID and returns the template, the owning project's display name,
    /// and the owning project's ID.
    /// </summary>
    public async Task<(PipelineJobTemplate? Template, string? ProjectName, string? ProjectId)> ResolveTemplateWithProjectAsync(
        TemplateId templateId, CancellationToken ct)
    {
        var (template, project) = await ResolveTemplateAndProjectAsync(templateId, ct);
        return (template, project?.Name, project?.Id);
    }

    /// <summary>
    /// Resolves a template by ID and returns it with its owning project, or (null, null) when the
    /// template is not in an enabled project.
    /// </summary>
    public async Task<(PipelineJobTemplate? Template, PipelineProject? Project)> ResolveTemplateAndProjectAsync(
        TemplateId templateId, CancellationToken ct)
    {
        // TODO [WARNING]: LoadProjectsAsync and LoadAllTemplatesAsync are two separate async calls with no
        // transactional consistency guarantee. If a project is deleted (or its TemplateIds updated) between
        // the two calls, a template in the lookup may have no owning project, returning (null, null, null)
        // and causing a rejected run. This race was present before the ProjectId fix but is now more
        // observable because both ProjectName and ProjectId are lost. Consider snapshotting both collections
        // in a single atomic read if the underlying store supports it.
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = (await _projectStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled))
        {
            // TODO [WARNING]: project.Id is a required string but there is no guard against an empty-string value
            // (e.g. a malformed config file with "id": ""). An empty Id passes all in-scope checks (Enabled=true,
            // TemplateIds.Contains) but Guid.TryParse in ConsolidationService.TriggerAsync will silently produce null,
            // reproducing the original null-ProjectId bug with no diagnostic. Consider validating that project.Id
            // is a non-empty, valid GUID here (or at the store/model level) and skipping/logging invalid projects.
            if (project.TemplateIds.Contains(templateId.Value) && templateLookup.TryGetValue(templateId.Value, out var template))
                return (template, project);
        }

        return (null, null);
    }
}
