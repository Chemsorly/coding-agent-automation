// Feature: 029-pipeline-projects
// Property 4: Default Project Invariant
// Verify that after any sequence of Create/Delete operations, the Default project
// always exists and cannot be deleted.
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.Persistence;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for the Default Project Invariant.
/// After any sequence of Create/Delete operations on an IProjectStore,
/// the Default project always exists and cannot be deleted.
/// **Validates: Requirements 2.1, 2.3, 12.7**
/// </summary>
public class DefaultProjectInvariantPropertyTests : IDisposable
{
    private readonly string _tempDir;

    public DefaultProjectInvariantPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"project-invariant-pbt-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 4a: Deleting the Default project always throws InvalidOperationException.
    /// For any prior sequence of Save/Delete operations, attempting to delete
    /// WellKnownIds.DefaultProjectId always throws.
    /// **Validates: Requirements 2.3, 12.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProjectStoreOperationArbitraries) })]
    public void DeleteDefaultProject_AlwaysThrows_InvalidOperationException(
        ProjectStoreOperation[] operations)
    {
        var store = CreateStoreWithDefaultProject();

        // Execute the random sequence of operations (ignoring failures)
        foreach (var op in operations)
        {
            try { ExecuteOperation(store, op); }
            catch { /* expected — some operations may fail by design */ }
        }

        // The invariant: deleting the Default project must always throw
        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.DeleteProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None)
                .GetAwaiter().GetResult());

        Assert.Contains("Default project cannot be deleted", ex.Message);
    }

    /// <summary>
    /// Property 4b: After any sequence of Create/Delete operations, GetProjectByIdAsync
    /// for the Default project always returns a non-null project.
    /// **Validates: Requirements 2.1, 2.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ProjectStoreOperationArbitraries) })]
    public void DefaultProject_AlwaysExists_AfterAnyOperationSequence(
        ProjectStoreOperation[] operations)
    {
        var store = CreateStoreWithDefaultProject();

        // Execute the random sequence of operations (ignoring failures)
        foreach (var op in operations)
        {
            try { ExecuteOperation(store, op); }
            catch { /* expected — some operations may fail by design */ }
        }

        // The invariant: the Default project must always exist
        var defaultProject = store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotNull(defaultProject);
        Assert.Equal(WellKnownIds.DefaultProjectId, defaultProject.Id);
    }

    private JsonConfigurationStore CreateStoreWithDefaultProject()
    {
        var store = new JsonConfigurationStore(_tempDir);

        // Seed the store with a Default project (simulating post-migration state)
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

    private static void ExecuteOperation(JsonConfigurationStore store, ProjectStoreOperation op)
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
/// Generates random sequences of Save and Delete operations, including
/// attempts to delete the Default project (which should always fail).
/// </summary>
public class ProjectStoreOperationArbitraries
{
    private static readonly string[] ProjectNamePool =
        ["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta"];

    private static readonly string[] TemplateIdPool =
        ["tmpl-001", "tmpl-002", "tmpl-003", "tmpl-004", "tmpl-005"];

    // A pool of project IDs to reuse across operations for more interesting sequences
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
            (3, saveGen),    // Save operations more frequent
            (2, deleteGen)); // Delete operations include Default project attempts

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
            from useDefaultId in Gen.Elements(false, false, false, true) // 25% chance to save over Default
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
        // Include the Default project ID in the delete targets to test the guard
        var allDeletableIds = NonDefaultProjectIdPool
            .Append(WellKnownIds.DefaultProjectId)
            .ToArray();

        return
            from id in Gen.Elements(allDeletableIds)
            select (ProjectStoreOperation)new ProjectStoreOperation.DeleteProject(id);
    }
}
