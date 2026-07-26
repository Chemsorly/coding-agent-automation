using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

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
    public async Task<PipelineJobTemplate?> ResolveTemplateAsync(string templateId, CancellationToken ct)
    {
        var (template, _) = await ResolveTemplateWithProjectAsync(templateId, ct);
        return template;
    }

    /// <summary>
    /// Resolves a template by ID and returns both the template and the owning project's display name.
    /// </summary>
    public async Task<(PipelineJobTemplate? Template, string? ProjectName)> ResolveTemplateWithProjectAsync(
        string templateId, CancellationToken ct)
    {
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = (await _projectStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

        foreach (var project in projects.Where(p => p.Enabled))
        {
            if (project.TemplateIds.Contains(templateId) && templateLookup.TryGetValue(templateId, out var template))
                return (template, project.Name);
        }

        return (null, null);
    }

    /// <summary>
    /// Returns all enabled templates from all enabled projects, resolved via IProjectStore.
    /// </summary>
    public async Task<IReadOnlyList<PipelineJobTemplate>> GetEnabledTemplatesFromProjectsAsync(CancellationToken ct)
    {
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var templateLookup = (await _projectStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

        var result = new List<PipelineJobTemplate>();
        foreach (var project in projects.Where(p => p.Enabled).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (var tid in project.TemplateIds)
            {
                if (templateLookup.TryGetValue(tid, out var template) && template.Enabled)
                    result.Add(template);
            }
        }

        return result;
    }
}
