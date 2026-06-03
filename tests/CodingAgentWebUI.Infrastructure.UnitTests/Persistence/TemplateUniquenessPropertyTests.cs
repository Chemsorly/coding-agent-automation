// Feature: 029-pipeline-projects
// Property 2: Template Uniqueness
// Verify a template ID appears in at most one project's TemplateIds at any time;
// conflicts resolved deterministically (first project alphabetically by name).
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for Template Uniqueness.
/// Each template ID must appear in at most one project after conflict resolution.
/// When a template ID appears in multiple projects, it is assigned to the first
/// project alphabetically by name.
/// **Validates: Requirements 1.4, 2.4**
/// </summary>
public class TemplateUniquenessPropertyTests
{
    /// <summary>
    /// Resolves template ownership conflicts across projects.
    /// When a template ID appears in multiple projects, it is assigned to the first
    /// project alphabetically by name. Returns the resolved project list with
    /// deduplicated TemplateIds.
    /// This implements the conflict resolution rule from Requirement 1.4.
    /// </summary>
    internal static IReadOnlyList<PipelineProject> ResolveTemplateConflicts(
        IReadOnlyList<PipelineProject> projects)
    {
        var assignedTemplates = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<PipelineProject>();

        // Process projects in alphabetical order by name (Ordinal comparison per design)
        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var deduplicatedIds = new List<string>();
            foreach (var templateId in project.TemplateIds)
            {
                if (assignedTemplates.Add(templateId))
                {
                    // First occurrence — this project owns the template
                    deduplicatedIds.Add(templateId);
                }
                // else: conflict — template already assigned to an earlier project alphabetically
            }

            resolved.Add(project with { TemplateIds = deduplicatedIds });
        }

        return resolved;
    }

    /// <summary>
    /// Property 2a: After conflict resolution, each template ID appears in at most one project.
    /// For any random set of projects with potentially overlapping template IDs,
    /// the resolved set has no duplicates across projects.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(TemplateUniquenessArbitraries) })]
    public void AfterResolution_EachTemplateId_AppearsInAtMostOneProject(
        PipelineProject[] projects)
    {
        var resolved = ResolveTemplateConflicts(projects);

        // Collect all template IDs across all resolved projects
        var allTemplateIds = resolved
            .SelectMany(p => p.TemplateIds)
            .ToList();

        var uniqueIds = new HashSet<string>(allTemplateIds, StringComparer.Ordinal);

        // Each template ID should appear exactly once across all projects
        Assert.Equal(uniqueIds.Count, allTemplateIds.Count);
    }

    /// <summary>
    /// Property 2b: Conflict resolution is deterministic — the first project alphabetically
    /// by name always wins ownership of a contested template ID.
    /// For any random set of projects where a template ID appears in multiple projects,
    /// after resolution, that template belongs to the project whose name comes first alphabetically.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(TemplateUniquenessArbitraries) })]
    public void ConflictResolution_FirstProjectAlphabetically_WinsOwnership(
        PipelineProject[] projects)
    {
        var resolved = ResolveTemplateConflicts(projects);

        // For each template that appeared in multiple projects in the input,
        // verify it ended up in the alphabetically-first project
        var templateToOriginalProjects = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            foreach (var templateId in project.TemplateIds)
            {
                if (!templateToOriginalProjects.TryGetValue(templateId, out var list))
                {
                    list = [];
                    templateToOriginalProjects[templateId] = list;
                }
                list.Add(project.Name);
            }
        }

        // For templates that were contested (in multiple projects)
        foreach (var (templateId, originalProjectNames) in templateToOriginalProjects)
        {
            if (originalProjectNames.Count <= 1) continue;

            // The expected winner is the first project name alphabetically
            var expectedWinner = originalProjectNames
                .OrderBy(n => n, StringComparer.Ordinal)
                .First();

            // Find which project owns this template after resolution
            var owningProject = resolved.FirstOrDefault(p => p.TemplateIds.Contains(templateId));

            Assert.NotNull(owningProject);
            Assert.Equal(expectedWinner, owningProject.Name);
        }
    }

    /// <summary>
    /// Property 2c: Resolution preserves all template IDs — no template is lost.
    /// The union of all template IDs after resolution equals the union of all template IDs
    /// before resolution. Templates are redistributed, not dropped.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(TemplateUniquenessArbitraries) })]
    public void AfterResolution_NoTemplateId_IsLost(
        PipelineProject[] projects)
    {
        var resolved = ResolveTemplateConflicts(projects);

        // All unique template IDs from the input
        var inputTemplateIds = new HashSet<string>(
            projects.SelectMany(p => p.TemplateIds),
            StringComparer.Ordinal);

        // All template IDs after resolution
        var resolvedTemplateIds = new HashSet<string>(
            resolved.SelectMany(p => p.TemplateIds),
            StringComparer.Ordinal);

        // Every input template ID should still exist somewhere after resolution
        Assert.Subset(inputTemplateIds, resolvedTemplateIds);
        Assert.Subset(resolvedTemplateIds, inputTemplateIds);
    }

    /// <summary>
    /// Property 2d: Resolution is deterministic — running resolution multiple times
    /// produces the same result.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(TemplateUniquenessArbitraries) })]
    public void Resolution_IsDeterministic_SameInputProducesSameOutput(
        PipelineProject[] projects)
    {
        var resolved1 = ResolveTemplateConflicts(projects);
        var resolved2 = ResolveTemplateConflicts(projects);

        Assert.Equal(resolved1.Count, resolved2.Count);

        for (int i = 0; i < resolved1.Count; i++)
        {
            Assert.Equal(resolved1[i].Name, resolved2[i].Name);
            Assert.Equal(resolved1[i].TemplateIds, resolved2[i].TemplateIds);
        }
    }
}

/// <summary>
/// FsCheck arbitrary generators for the Template Uniqueness property tests.
/// Generates random sets of projects with potentially overlapping template IDs
/// to stress-test the conflict resolution logic.
/// </summary>
public class TemplateUniquenessArbitraries
{
    private static readonly string[] ProjectNamePool =
        ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta"];

    private static readonly string[] TemplateIdPool =
        ["tmpl-001", "tmpl-002", "tmpl-003", "tmpl-004", "tmpl-005",
         "tmpl-006", "tmpl-007", "tmpl-008", "tmpl-009", "tmpl-010"];

    public static Arbitrary<PipelineProject[]> ProjectArrayArb()
    {
        var projectGen =
            from name in Gen.Elements(ProjectNamePool)
            from templateCount in Gen.Choose(0, 5)
            from templateIds in Gen.ArrayOf(Gen.Elements(TemplateIdPool)).Resize(templateCount)
            from enabled in Gen.Elements(true, true, true, false) // 75% enabled
            select new PipelineProject
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Enabled = enabled,
                TemplateIds = templateIds.Distinct().ToArray()
            };

        var projectArrayGen =
            from count in Gen.Choose(2, 6) // At least 2 projects to allow conflicts
            from projects in Gen.ArrayOf(projectGen).Resize(count)
            select EnsureUniqueNames(projects);

        return projectArrayGen.ToArbitrary();
    }

    /// <summary>
    /// Ensures projects have unique names by appending a suffix if duplicates exist.
    /// This is needed because the resolution uses name as the deterministic tiebreaker.
    /// </summary>
    private static PipelineProject[] EnsureUniqueNames(PipelineProject[] projects)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new PipelineProject[projects.Length];

        for (int i = 0; i < projects.Length; i++)
        {
            var name = projects[i].Name;
            if (seen.TryGetValue(name, out var count))
            {
                seen[name] = count + 1;
                result[i] = projects[i] with { Name = $"{name}_{count + 1}" };
            }
            else
            {
                seen[name] = 1;
                result[i] = projects[i];
            }
        }

        return result;
    }
}
