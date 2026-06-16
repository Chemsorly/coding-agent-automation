using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

public class ProjectStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigurationStore _store;

    public ProjectStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"project-store-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigurationStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Save/Load/Delete cycle ──────────────────────────────────────────────

    [Fact]
    public async Task SaveProjectAsync_ThenLoadProjectsAsync_ReturnsTheSavedProject()
    {
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Project",
            Description = "A test project",
            Enabled = true,
            TemplateIds = ["template-1", "template-2"]
        };

        await _store.SaveProjectAsync(project, CancellationToken.None);
        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);

        // +1 for the auto-created Default project
        loaded.Should().HaveCount(2);
        var result = loaded.Single(p => p.Id == project.Id);
        result.Name.Should().Be("Test Project");
        result.Description.Should().Be("A test project");
        result.Enabled.Should().BeTrue();
        result.TemplateIds.Should().BeEquivalentTo(["template-1", "template-2"]);
    }

    [Fact]
    public async Task GetProjectByIdAsync_ReturnsSavedProject()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = projectId,
            Name = "Lookup Project",
            TemplateIds = ["t1"]
        };

        await _store.SaveProjectAsync(project, CancellationToken.None);
        var result = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(projectId);
        result.Name.Should().Be("Lookup Project");
    }

    [Fact]
    public async Task GetProjectByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _store.GetProjectByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteProjectAsync_RemovesProjectFile()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject
        {
            Id = projectId,
            Name = "To Delete"
        };

        // Save a Default project first (required for template move on delete)
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default"
        };
        await _store.SaveProjectAsync(defaultProject, CancellationToken.None);
        await _store.SaveProjectAsync(project, CancellationToken.None);

        await _store.DeleteProjectAsync(projectId, CancellationToken.None);

        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);
        loaded.Should().NotContain(p => p.Id == projectId);
    }

    [Fact]
    public async Task DeleteProjectAsync_MovesOrphanedTemplatesToDefault()
    {
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = ["existing-template"]
        };
        var projectToDelete = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Doomed Project",
            TemplateIds = ["orphan-1", "orphan-2"]
        };

        await _store.SaveProjectAsync(defaultProject, CancellationToken.None);
        await _store.SaveProjectAsync(projectToDelete, CancellationToken.None);

        await _store.DeleteProjectAsync(projectToDelete.Id, CancellationToken.None);

        var updatedDefault = await _store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        updatedDefault.Should().NotBeNull();
        updatedDefault!.TemplateIds.Should().BeEquivalentTo(
            ["existing-template", "orphan-1", "orphan-2"]);
    }

    [Fact]
    public async Task SaveProjectAsync_OverwritesExistingProject()
    {
        var projectId = Guid.NewGuid().ToString();
        var original = new PipelineProject
        {
            Id = projectId,
            Name = "Original Name"
        };
        await _store.SaveProjectAsync(original, CancellationToken.None);

        var updated = original with { Name = "Updated Name", Description = "Now with description" };
        await _store.SaveProjectAsync(updated, CancellationToken.None);

        var result = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Description.Should().Be("Now with description");
    }

    [Fact]
    public async Task LoadProjectsAsync_EmptyDirectory_ReturnsOnlyDefaultProject()
    {
        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);

        loaded.Should().HaveCount(1);
        loaded[0].Id.Should().Be(WellKnownIds.DefaultProjectId);
        loaded[0].Name.Should().Be("Default");
    }

    // ── Invalid JSON handling (skip + warning) ──────────────────────────────

    [Fact]
    public async Task LoadProjectsAsync_InvalidJsonFile_SkipsCorruptedAndLoadsOthers()
    {
        // Create the projects directory with a malformed file
        var projectsDir = Path.Combine(_tempDir, "projects");
        Directory.CreateDirectory(projectsDir);
        await File.WriteAllTextAsync(
            Path.Combine(projectsDir, "bad-project.json"),
            "{ this is not valid json!!!");

        // Also save a valid project
        var validProject = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Valid Project"
        };
        await _store.SaveProjectAsync(validProject, CancellationToken.None);

        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);

        // Default project + valid project (bad-project.json skipped)
        loaded.Should().HaveCount(2);
        loaded.Should().Contain(p => p.Name == "Valid Project");
        loaded.Should().Contain(p => p.Id == WellKnownIds.DefaultProjectId);
    }

    [Fact]
    public async Task GetProjectByIdAsync_InvalidGuidFormat_ReturnsNull()
    {
        var result = await _store.GetProjectByIdAsync("not-a-guid", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Concurrent access safety ────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentSaves_DoNotCorruptData()
    {
        var projectIds = Enumerable.Range(0, 20)
            .Select(_ => Guid.NewGuid().ToString())
            .ToList();

        // Concurrently save 20 projects
        var tasks = projectIds.Select(id =>
            _store.SaveProjectAsync(
                new PipelineProject { Id = id, Name = $"Project-{id[..8]}" },
                CancellationToken.None));

        await Task.WhenAll(tasks);

        // All projects should be persisted correctly (+1 for auto-created Default project)
        var loaded = await _store.LoadProjectsAsync(CancellationToken.None);
        loaded.Should().HaveCount(21);

        foreach (var id in projectIds)
        {
            loaded.Should().Contain(p => p.Id == id);
        }
    }

    [Fact]
    public async Task ConcurrentSaves_ToSameProject_DoNotCorruptFile()
    {
        var projectId = Guid.NewGuid().ToString();

        // Rapidly save updates to the same project concurrently
        var tasks = Enumerable.Range(0, 10).Select(i =>
            _store.SaveProjectAsync(
                new PipelineProject { Id = projectId, Name = $"Update-{i}" },
                CancellationToken.None));

        await Task.WhenAll(tasks);

        // The file should contain one of the valid updates (last writer wins)
        var result = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.Name.Should().StartWith("Update-");
    }

    // ── Default project cannot be deleted ───────────────────────────────────

    [Fact]
    public async Task DeleteProjectAsync_DefaultProject_ThrowsInvalidOperationException()
    {
        // Ensure the Default project exists
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default"
        };
        await _store.SaveProjectAsync(defaultProject, CancellationToken.None);

        var act = () => _store.DeleteProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Default project cannot be deleted*");
    }

    [Fact]
    public async Task DeleteProjectAsync_DefaultProject_PreservesProjectFile()
    {
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = ["t1", "t2"]
        };
        await _store.SaveProjectAsync(defaultProject, CancellationToken.None);

        try
        {
            await _store.DeleteProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // Verify the project still exists
        var result = await _store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        result.Should().NotBeNull();
        result!.TemplateIds.Should().BeEquivalentTo(["t1", "t2"]);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveProjectAsync_InvalidGuidId_ThrowsArgumentException()
    {
        var project = new PipelineProject
        {
            Id = "not-a-guid",
            Name = "Bad ID Project"
        };

        var act = () => _store.SaveProjectAsync(project, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not a valid GUID format*");
    }

    [Fact]
    public async Task SaveProjectAsync_EmptyName_ThrowsArgumentException()
    {
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = ""
        };

        var act = () => _store.SaveProjectAsync(project, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*name must be non-empty*");
    }

    [Fact]
    public async Task SaveProjectAsync_NameExceeds128Chars_ThrowsArgumentException()
    {
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = new string('A', 129)
        };

        var act = () => _store.SaveProjectAsync(project, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*must not exceed 128 characters*");
    }

    [Fact]
    public async Task SaveProjectAsync_DescriptionExceeds512Chars_ThrowsArgumentException()
    {
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Valid Name",
            Description = new string('X', 513)
        };

        var act = () => _store.SaveProjectAsync(project, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*must not exceed 512 characters*");
    }

    // ── Template CRUD ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveTemplateAsync_ThenLoadTemplatesForProjectAsync_ReturnsTemplate()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var loaded = await _store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None);
        loaded.Should().HaveCount(1);
        loaded[0].Id.Should().Be(template.Id);
        loaded[0].Name.Should().Be("T1");
    }

    [Fact]
    public async Task SaveTemplateAsync_AddsTemplateIdToProjectTemplateIds()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var updatedProject = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);
        updatedProject!.TemplateIds.Should().Contain(template.Id);
    }

    [Fact]
    public async Task SaveTemplateAsync_IdempotentSave_DoesNotDuplicateTemplateId()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);

        var updatedProject = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);
        updatedProject!.TemplateIds.Count(id => id == template.Id).Should().Be(1);
    }

    [Fact]
    public async Task DeleteTemplateAsync_RemovesTemplateAndUpdatesTemplateIds()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "T1",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await _store.SaveTemplateAsync(projectId, template, CancellationToken.None);
        await _store.DeleteTemplateAsync(projectId, template.Id, CancellationToken.None);

        var loaded = await _store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None);
        loaded.Should().BeEmpty();

        var updatedProject = await _store.GetProjectByIdAsync(projectId, CancellationToken.None);
        updatedProject!.TemplateIds.Should().NotContain(template.Id);
    }

    [Fact]
    public async Task MoveTemplateAsync_MovesTemplateBetweenProjects()
    {
        var sourceId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();
        await _store.SaveProjectAsync(new PipelineProject { Id = sourceId, Name = "Source" }, CancellationToken.None);
        await _store.SaveProjectAsync(new PipelineProject { Id = targetId, Name = "Target" }, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Movable",
            IssueProviderId = "ip1",
            RepoProviderId = "rp1"
        };
        await _store.SaveTemplateAsync(sourceId, template, CancellationToken.None);

        await _store.MoveTemplateAsync(sourceId, targetId, template.Id, CancellationToken.None);

        var sourceTemplates = await _store.LoadTemplatesForProjectAsync(sourceId, CancellationToken.None);
        sourceTemplates.Should().BeEmpty();

        var targetTemplates = await _store.LoadTemplatesForProjectAsync(targetId, CancellationToken.None);
        targetTemplates.Should().HaveCount(1);
        targetTemplates[0].Id.Should().Be(template.Id);

        var sourceProject = await _store.GetProjectByIdAsync(sourceId, CancellationToken.None);
        sourceProject!.TemplateIds.Should().NotContain(template.Id);

        var targetProject = await _store.GetProjectByIdAsync(targetId, CancellationToken.None);
        targetProject!.TemplateIds.Should().Contain(template.Id);
    }

    [Fact]
    public async Task LoadTemplatesForProjectAsync_OrderedByTemplateIds()
    {
        var projectId = Guid.NewGuid().ToString();
        var project = new PipelineProject { Id = projectId, Name = "TP" };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var t1 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "First", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t2 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "Second", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t3 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "Third", IssueProviderId = "ip1", RepoProviderId = "rp1" };

        await _store.SaveTemplateAsync(projectId, t1, CancellationToken.None);
        await _store.SaveTemplateAsync(projectId, t2, CancellationToken.None);
        await _store.SaveTemplateAsync(projectId, t3, CancellationToken.None);

        var loaded = await _store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None);
        loaded.Should().HaveCount(3);
        loaded[0].Id.Should().Be(t1.Id);
        loaded[1].Id.Should().Be(t2.Id);
        loaded[2].Id.Should().Be(t3.Id);
    }

    [Fact]
    public async Task LoadAllTemplatesAsync_ReturnsTemplatesAcrossProjects()
    {
        var p1 = Guid.NewGuid().ToString();
        var p2 = Guid.NewGuid().ToString();
        await _store.SaveProjectAsync(new PipelineProject { Id = p1, Name = "P1" }, CancellationToken.None);
        await _store.SaveProjectAsync(new PipelineProject { Id = p2, Name = "P2" }, CancellationToken.None);

        var t1 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "T1", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t2 = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "T2", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        await _store.SaveTemplateAsync(p1, t1, CancellationToken.None);
        await _store.SaveTemplateAsync(p2, t2, CancellationToken.None);

        var all = await _store.LoadAllTemplatesAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all.Select(t => t.Id).Should().Contain(t1.Id);
        all.Select(t => t.Id).Should().Contain(t2.Id);
    }
}
