using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for IProjectStore CRUD operations using InMemoryConfigurationStore.
/// Migrated from JsonConfigurationStore by Spec 041.
/// </summary>
public class ProjectStoreTests
{
    private static InMemoryConfigurationStore CreateStore()
    {
        var store = new InMemoryConfigurationStore();
        // Ensure Default project exists (InMemoryConfigurationStore seeds it via SeedDefaults)
        return store;
    }

    // ── Save/Load/Delete cycle ──────────────────────────────────────────────

    [Fact]
    public async Task SaveProjectAsync_ThenLoadProjectsAsync_ReturnsTheSavedProject()
    {
        var store = CreateStore();
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Project",
            Description = "A test project",
            Enabled = true,
            TemplateIds = ["template-1", "template-2"]
        };

        await store.SaveProjectAsync(project, CancellationToken.None);
        var loaded = await store.LoadProjectsAsync(CancellationToken.None);

        loaded.Should().Contain(p => p.Id == project.Id);
        var result = loaded.Single(p => p.Id == project.Id);
        result.Name.Should().Be("Test Project");
        result.Description.Should().Be("A test project");
        result.Enabled.Should().BeTrue();
        result.TemplateIds.Should().BeEquivalentTo(["template-1", "template-2"]);
    }

    [Fact]
    public async Task GetProjectByIdAsync_ReturnsSavedProject()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = projectId,
            Name = "Lookup Project",
            TemplateIds = ["t1"]
        };

        await store.SaveProjectAsync(project, CancellationToken.None);
        var result = await store.GetProjectByIdAsync(projectId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(projectId);
        result.Name.Should().Be("Lookup Project");
    }

    [Fact]
    public async Task GetProjectByIdAsync_NonExistentId_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetProjectByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteProjectAsync_RemovesProject()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "To Delete" };
        await store.SaveProjectAsync(project, CancellationToken.None);

        await store.DeleteProjectAsync(projectId, CancellationToken.None);

        var loaded = await store.LoadProjectsAsync(CancellationToken.None);
        loaded.Should().NotContain(p => p.Id == projectId);
    }

    [Fact]
    public async Task SaveProjectAsync_OverwritesExistingProject()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var original = new PipelineProject { Id = projectId, Name = "Original Name" };
        await store.SaveProjectAsync(original, CancellationToken.None);

        var updated = original with { Name = "Updated Name", Description = "Now with description" };
        await store.SaveProjectAsync(updated, CancellationToken.None);

        var result = await store.GetProjectByIdAsync(projectId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Description.Should().Be("Now with description");
    }

    [Fact]
    public async Task LoadProjectsAsync_ContainsDefaultProject()
    {
        var store = CreateStore();
        var loaded = await store.LoadProjectsAsync(CancellationToken.None);

        loaded.Should().Contain(p => p.Id == WellKnownIds.DefaultProjectId);
    }

    // ── Template CRUD ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveTemplateAsync_ThenLoadTemplatesForProjectAsync_ReturnsTemplate()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var loaded = await store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None);
        loaded.Should().Contain(t => t.Id == template.Id);
        loaded.Single(t => t.Id == template.Id).Name.Should().Be("T1");
    }

    [Fact]
    public async Task SaveTemplateAsync_AddsTemplateIdToProjectTemplateIds()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var updatedProject = await store.GetProjectByIdAsync(projectId, CancellationToken.None);
        updatedProject!.TemplateIds.Should().Contain(template.Id);
    }

    [Fact]
    public async Task DeleteTemplateAsync_RemovesTemplateAndUpdatesTemplateIds()
    {
        var store = CreateStore();
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await store.SaveTemplateAsync(projectId, template, CancellationToken.None);
        await store.DeleteTemplateAsync(projectId, template.Id, CancellationToken.None);

        var loaded = await store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None);
        loaded.Should().NotContain(t => t.Id == template.Id);

        var updatedProject = await store.GetProjectByIdAsync(projectId, CancellationToken.None);
        updatedProject!.TemplateIds.Should().NotContain(template.Id);
    }

    [Fact]
    public async Task MoveTemplateAsync_MovesTemplateBetweenProjects()
    {
        var store = CreateStore();
        var sourceId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();
        await store.SaveProjectAsync(new PipelineProject { Id = sourceId, Name = "Source" }, CancellationToken.None);
        await store.SaveProjectAsync(new PipelineProject { Id = targetId, Name = "Target" }, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Movable",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await store.SaveTemplateAsync(sourceId, template, CancellationToken.None);

        await store.MoveTemplateAsync(sourceId, targetId, template.Id, CancellationToken.None);

        var sourceProject = await store.GetProjectByIdAsync(sourceId, CancellationToken.None);
        sourceProject!.TemplateIds.Should().NotContain(template.Id);

        var targetProject = await store.GetProjectByIdAsync(targetId, CancellationToken.None);
        targetProject!.TemplateIds.Should().Contain(template.Id);
    }

    [Fact]
    public async Task LoadAllTemplatesAsync_ReturnsTemplatesAcrossProjects()
    {
        var store = CreateStore();
        var p1 = Guid.NewGuid().ToString();
        var p2 = Guid.NewGuid().ToString();
        await store.SaveProjectAsync(new PipelineProject { Id = p1, Name = "P1" }, CancellationToken.None);
        await store.SaveProjectAsync(new PipelineProject { Id = p2, Name = "P2" }, CancellationToken.None);

        var t1 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "T1", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t2 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "T2", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        await store.SaveTemplateAsync(p1, t1, CancellationToken.None);
        await store.SaveTemplateAsync(p2, t2, CancellationToken.None);

        var all = await store.LoadAllTemplatesAsync(CancellationToken.None);
        all.Select(t => t.Id).Should().Contain(t1.Id);
        all.Select(t => t.Id).Should().Contain(t2.Id);
    }
}
