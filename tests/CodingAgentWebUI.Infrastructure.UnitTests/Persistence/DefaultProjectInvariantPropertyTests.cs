// Feature: 029-pipeline-projects
// Property 4: Default Project Invariant
// Verify that after any sequence of Create/Delete operations, the Default project
// round-trips correctly via IConfigurationStore.
// Migrated to InMemoryConfigurationStore by Spec 041 (JsonConfigurationStore removed).
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests verifying basic project store round-trip invariants.
/// Uses InMemoryConfigurationStore (promoted from E2ETests by Spec 041).
/// **Validates: Requirements 2.1, 2.3, 12.7**
/// </summary>
public class DefaultProjectInvariantPropertyTests
{
    /// <summary>
    /// Property 4a: After any sequence of Save/Delete operations, a project that was saved
    /// and not deleted can always be retrieved by GetProjectByIdAsync.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProjectStoreOperationArbitraries) })]
    public void SavedProject_AlwaysRetrievable_UnlessDeleted(
        ProjectStoreOperation[] operations)
    {
        var store = CreateStoreWithDefaultProject();

        // Track which projects were last saved vs deleted
        var saved = new HashSet<string>();
        var deleted = new HashSet<string>();

        foreach (var op in operations)
        {
            try
            {
                ExecuteOperation(store, op);
                switch (op)
                {
                    case ProjectStoreOperation.SaveProject save:
                        saved.Add(save.Project.Id);
                        deleted.Remove(save.Project.Id);
                        break;
                    case ProjectStoreOperation.DeleteProject del:
                        // Default project delete may throw — tolerate both outcomes
                        deleted.Add(del.ProjectId);
                        saved.Remove(del.ProjectId);
                        break;
                }
            }
            catch { /* tolerate expected failures */ }
        }

        // All saved (and not deleted) non-default projects must be retrievable
        foreach (var id in saved.Except(deleted).Where(id => id != WellKnownIds.DefaultProjectId))
        {
            var found = store.GetProjectByIdAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            Assert.NotNull(found);
        }
    }

    /// <summary>
    /// Property 4b: Saving then loading the Default project round-trips correctly.
    /// **Validates: Requirements 2.1, 2.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProjectStoreOperationArbitraries) })]
    public void DefaultProject_WhenSaved_IsRetrievable(
        ProjectStoreOperation[] operations)
    {
        var store = CreateStoreWithDefaultProject();

        foreach (var op in operations)
        {
            try { ExecuteOperation(store, op); }
            catch { /* expected */ }
        }

        // Re-save default project to ensure it exists regardless of what operations did
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            Enabled = true,
            TemplateIds = []
        };
        store.SaveProjectAsync(defaultProject, CancellationToken.None).GetAwaiter().GetResult();

        var found = store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotNull(found);
        Assert.Equal(WellKnownIds.DefaultProjectId, found.Id);
    }

    private static InMemoryConfigurationStore CreateStoreWithDefaultProject()
    {
        var store = new InMemoryConfigurationStore();

        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            Enabled = true,
            TemplateIds = []
        };

        store.SaveProjectAsync(defaultProject, CancellationToken.None).GetAwaiter().GetResult();
        return store;
    }

    private static void ExecuteOperation(InMemoryConfigurationStore store, ProjectStoreOperation op)
    {
        switch (op)
        {
            case ProjectStoreOperation.SaveProject save:
                store.SaveProjectAsync(save.Project, CancellationToken.None).GetAwaiter().GetResult();
                break;

            case ProjectStoreOperation.DeleteProject delete:
                store.DeleteProjectAsync(delete.ProjectId, CancellationToken.None).GetAwaiter().GetResult();
                break;
        }
    }
}

/// <summary>
/// Discriminated union representing operations on the project store.
/// </summary>
public abstract record ProjectStoreOperation
{
    public sealed record SaveProject(PipelineProject Project) : ProjectStoreOperation;
    public sealed record DeleteProject(string ProjectId) : ProjectStoreOperation;
}

/// <summary>
/// FsCheck arbitrary generators for project store operations.
/// </summary>
public class ProjectStoreOperationArbitraries
{
    private static readonly string[] ProjectNamePool =
        ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta"];

    private static readonly string[] TemplateIdPool =
        ["tmpl-001", "tmpl-002", "tmpl-003", "tmpl-004", "tmpl-005"];

    private static readonly string[] NonDefaultProjectIdPool =
    [
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        "33333333-3333-3333-3333-333333333333",
        "44444444-4444-4444-4444-444444444444"
    ];

    public static Arbitrary<ProjectStoreOperation[]> OperationSequenceArb()
    {
        var saveGen = GenSaveOperation();
        var deleteGen = GenDeleteOperation();

        var operationGen = Gen.Frequency<ProjectStoreOperation>(
            (3, saveGen),
            (2, deleteGen));

        var sequenceGen =
            from count in Gen.Choose(1, 10)
            from ops in Gen.ArrayOf(operationGen).Resize(count)
            select ops;

        return sequenceGen.ToArbitrary();
    }

    private static Gen<ProjectStoreOperation> GenSaveOperation()
    {
        return
            from name in Gen.Elements(ProjectNamePool)
            from useDefaultId in Gen.Elements(false, false, false, true)
            from projectId in Gen.Elements(NonDefaultProjectIdPool)
            from templateCount in Gen.Choose(0, 3)
            from templateIds in Gen.ArrayOf(Gen.Elements(TemplateIdPool)).Resize(templateCount)
            from enabled in Gen.Elements(true, false)
            select (ProjectStoreOperation)new ProjectStoreOperation.SaveProject(new PipelineProject
            {
                Id = useDefaultId ? WellKnownIds.DefaultProjectId : projectId,
                Name = useDefaultId ? "Default" : name,
                Enabled = enabled,
                TemplateIds = templateIds.Distinct().ToArray()
            });
    }

    private static Gen<ProjectStoreOperation> GenDeleteOperation()
    {
        var allDeletableIds = NonDefaultProjectIdPool
            .Append(WellKnownIds.DefaultProjectId)
            .ToArray();

        return
            from id in Gen.Elements(allDeletableIds)
            select (ProjectStoreOperation)new ProjectStoreOperation.DeleteProject(id);
    }
}
