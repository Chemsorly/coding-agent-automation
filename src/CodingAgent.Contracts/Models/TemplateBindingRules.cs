namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The rules a template must meet when it is saved or moved into a project. Among enabled templates:
/// <list type="bullet">
/// <item>a repository belongs to one template;</item>
/// <item>an issue tracker belongs to one template;</item>
/// <item>a name is unique within its project, because the name is how a project epic routes its sub-issues.</item>
/// </list>
/// Disabled templates are not checked, so one of two conflicting templates can always be switched off.
/// A project's epic tracker may also be the tracker of one of its templates; that is not a template binding.
/// </summary>
public static class TemplateBindingRules
{
    /// <summary>
    /// Returns why <paramref name="template"/> cannot be saved into <paramref name="projectId"/>,
    /// or <c>null</c> when it can.
    /// </summary>
    /// <param name="allTemplates">Every saved template, in any project. May contain the template itself.</param>
    /// <param name="projects">Every project, with its <see cref="PipelineProject.TemplateIds"/>.</param>
    public static string? Validate(
        PipelineJobTemplate template,
        string projectId,
        IReadOnlyCollection<PipelineJobTemplate> allTemplates,
        IReadOnlyCollection<PipelineProject> projects)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
            return "A template needs a name.";

        if (!template.Enabled)
            return null;

        var others = allTemplates.Where(t => t.Enabled && t.Id != template.Id).ToList();

        var sameRepo = others.FirstOrDefault(t => SameBinding(t.RepoProviderId, template.RepoProviderId));
        if (sameRepo is not null)
            return $"The repository is already used by the enabled template \"{sameRepo.Name}\". " +
                "A repository belongs to one enabled template.";

        var sameTracker = others.FirstOrDefault(t => SameBinding(t.IssueProviderId, template.IssueProviderId));
        if (sameTracker is not null)
            return $"The issue tracker is already used by the enabled template \"{sameTracker.Name}\". " +
                "An issue tracker belongs to one enabled template.";

        var memberIds = projects.FirstOrDefault(p => p.Id == projectId)?.TemplateIds ?? [];
        var sameName = others.FirstOrDefault(t => memberIds.Contains(t.Id) && SameName(t.Name, template.Name));
        if (sameName is not null)
            return $"The project already has an enabled template named \"{sameName.Name}\". " +
                "Template names are unique within a project.";

        return null;
    }

    /// <summary>
    /// Returns one message per group of enabled templates that break the rules, for templates saved
    /// before the rules were enforced.
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(
        IReadOnlyCollection<PipelineJobTemplate> allTemplates,
        IReadOnlyCollection<PipelineProject> projects)
    {
        var enabled = allTemplates.Where(t => t.Enabled).ToList();
        var conflicts = new List<string>();

        AddConflicts(conflicts,
            enabled.Where(t => !string.IsNullOrEmpty(t.RepoProviderId)).GroupBy(t => t.RepoProviderId, StringComparer.Ordinal),
            "use the same repository");
        AddConflicts(conflicts,
            enabled.Where(t => !string.IsNullOrEmpty(t.IssueProviderId)).GroupBy(t => t.IssueProviderId, StringComparer.Ordinal),
            "use the same issue tracker");

        foreach (var project in projects)
        {
            var members = enabled.Where(t => project.TemplateIds.Contains(t.Id));
            AddConflicts(conflicts,
                members.GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase),
                $"have the same name in project \"{project.Name}\"");
        }

        return conflicts;
    }

    private static void AddConflicts(
        List<string> conflicts, IEnumerable<IGrouping<string, PipelineJobTemplate>> groups, string reason)
    {
        foreach (var group in groups)
        {
            var names = group.Select(t => $"\"{t.Name}\"").ToList();
            if (names.Count > 1)
                conflicts.Add($"Templates {string.Join(", ", names)} {reason}.");
        }
    }

    private static bool SameBinding(string? existing, string? candidate) =>
        !string.IsNullOrEmpty(candidate) && string.Equals(existing, candidate, StringComparison.Ordinal);

    private static bool SameName(string existing, string candidate) =>
        string.Equals(existing.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);
}
