// Feature: 029-pipeline-projects
// Task 11.4: Unit tests for migration
// Tests for ProjectMigrationService.MigrateToProjectsAsync and EnsureDefaultProjectExistsAsync.
// **Validates: Requirements 2.5, 11.1, 11.4, 11.5**
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for ProjectMigrationService covering:
/// - Fresh migration creates Default project with all template IDs
/// - Idempotency (run twice = same result)
/// - Already-migrated is no-op
/// - Self-healing recreates Default if corrupted/missing
/// </summary>
public class ProjectMigrationServiceTests
{
    private readonly Mock<IProjectStore> _projectStore;
    private readonly Mock<IPipelineConfigStore> _configStore;
    private readonly CancellationToken _ct = CancellationToken.None;

    public ProjectMigrationServiceTests()
    {
        _projectStore = new Mock<IProjectStore>(MockBehavior.Strict);
        _configStore = new Mock<IPipelineConfigStore>(MockBehavior.Strict);
    }

    #region Fresh Migration

    [Fact]
    public async Task MigrateToProjectsAsync_NoProjectsExist_ThreeTemplates_CreatesDefaultWithAllTemplateIds()
    {
        // Arrange: No projects exist, 3 templates in config
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate("tmpl-1", "DotNet-Main"),
            CreateTemplate("tmpl-2", "Java-Backend"),
            CreateTemplate("tmpl-3", "Python-ML")
        };

        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = templates
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(new List<PipelineProject>());

        _configStore.Setup(s => s.LoadPipelineConfigAsync(_ct))
            .ReturnsAsync(config);

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert
        savedProject.Should().NotBeNull();
        savedProject!.Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProject.Name.Should().Be("Default");
        savedProject.TemplateIds.Should().HaveCount(3);
        savedProject.TemplateIds.Should().ContainInOrder("tmpl-1", "tmpl-2", "tmpl-3");

        _projectStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct), Times.Once);
    }

    [Fact]
    public async Task MigrateToProjectsAsync_NoProjectsExist_NoTemplates_CreatesEmptyDefaultProject()
    {
        // Arrange: No projects exist, no templates in config
        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = new List<PipelineJobTemplate>()
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(new List<PipelineProject>());

        _configStore.Setup(s => s.LoadPipelineConfigAsync(_ct))
            .ReturnsAsync(config);

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert
        savedProject.Should().NotBeNull();
        savedProject!.Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProject.Name.Should().Be("Default");
        savedProject.TemplateIds.Should().BeEmpty();
    }

    [Fact]
    public async Task MigrateToProjectsAsync_NoProjectsExist_PreservesTemplateOrder()
    {
        // Arrange: Templates in a specific order
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate("zzz-last", "Zulu"),
            CreateTemplate("aaa-first", "Alpha"),
            CreateTemplate("mmm-middle", "Mike")
        };

        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = templates
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(new List<PipelineProject>());

        _configStore.Setup(s => s.LoadPipelineConfigAsync(_ct))
            .ReturnsAsync(config);

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert: template IDs should preserve their original order from PipelineJobTemplates
        savedProject!.TemplateIds.Should().ContainInOrder("zzz-last", "aaa-first", "mmm-middle");
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task MigrateToProjectsAsync_RunTwice_SameResult()
    {
        // Arrange: Simulate first run creating the Default project, then second run finding it
        var templates = new List<PipelineJobTemplate>
        {
            CreateTemplate("tmpl-1", "DotNet-Main"),
            CreateTemplate("tmpl-2", "Java-Backend")
        };

        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = templates
        };

        var savedProjects = new List<PipelineProject>();

        // First call: no projects exist
        var callCount = 0;
        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call returns empty (no migration yet), second call returns saved projects
                return callCount == 1
                    ? new List<PipelineProject>()
                    : savedProjects.ToList().AsReadOnly();
            });

        _configStore.Setup(s => s.LoadPipelineConfigAsync(_ct))
            .ReturnsAsync(config);

        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProjects.Add(p))
            .Returns(Task.CompletedTask);

        // Act: Run migration twice
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert: Only ONE save should have happened (second run is no-op since projects exist)
        savedProjects.Should().HaveCount(1);
        savedProjects[0].Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProjects[0].TemplateIds.Should().HaveCount(2);
    }

    #endregion

    #region Already Migrated (No-Op)

    [Fact]
    public async Task MigrateToProjectsAsync_ProjectsAlreadyExist_IsNoOp()
    {
        // Arrange: Projects already exist including the Default project
        var existingProjects = new List<PipelineProject>
        {
            new()
            {
                Id = WellKnownIds.DefaultProjectId,
                Name = "Default",
                TemplateIds = ["tmpl-1", "tmpl-2"]
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Java Project",
                TemplateIds = ["tmpl-3"]
            }
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(existingProjects);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert: No config load, no project saves — complete no-op
        _configStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        _projectStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MigrateToProjectsAsync_ProjectsExistWithDifferentTemplates_DoesNotReassign()
    {
        // Arrange: Default project exists with only 1 template, config has 3 templates.
        // Migration should NOT reassign templates since projects already exist.
        var existingProjects = new List<PipelineProject>
        {
            new()
            {
                Id = WellKnownIds.DefaultProjectId,
                Name = "Default",
                TemplateIds = ["tmpl-1"]
            }
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(existingProjects);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert: No reassignment — the migration is a no-op when projects exist
        _configStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        _projectStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Self-Healing (EnsureDefaultProjectExists)

    [Fact]
    public async Task EnsureDefaultProjectExistsAsync_DefaultPresent_DoesNothing()
    {
        // Arrange: Default project is present among existing projects
        var projects = new List<PipelineProject>
        {
            new()
            {
                Id = WellKnownIds.DefaultProjectId,
                Name = "Default",
                TemplateIds = ["tmpl-1"]
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Other Project",
                TemplateIds = ["tmpl-2"]
            }
        };

        // Act
        await ProjectMigrationService.EnsureDefaultProjectExistsAsync(projects, _projectStore.Object, _ct);

        // Assert: No save was called
        _projectStore.Verify(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureDefaultProjectExistsAsync_DefaultMissing_RecreatesWithEmptyTemplateIds()
    {
        // Arrange: Projects exist but Default is NOT among them (corrupted/deleted)
        var projects = new List<PipelineProject>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Java Project",
                TemplateIds = ["tmpl-1"]
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Python Project",
                TemplateIds = ["tmpl-2"]
            }
        };

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.EnsureDefaultProjectExistsAsync(projects, _projectStore.Object, _ct);

        // Assert: Default recreated with empty TemplateIds
        savedProject.Should().NotBeNull();
        savedProject!.Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProject.Name.Should().Be("Default");
        savedProject.TemplateIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureDefaultProjectExistsAsync_EmptyProjectList_RecreatesDefault()
    {
        // Arrange: No projects at all (all corrupted)
        var projects = new List<PipelineProject>();

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.EnsureDefaultProjectExistsAsync(projects, _projectStore.Object, _ct);

        // Assert
        savedProject.Should().NotBeNull();
        savedProject!.Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProject.Name.Should().Be("Default");
    }

    [Fact]
    public async Task MigrateToProjectsAsync_ProjectsExistButDefaultMissing_SelfHeals()
    {
        // Arrange: Projects exist but Default is missing (triggers self-healing path)
        var existingProjects = new List<PipelineProject>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Orphaned Project",
                TemplateIds = ["tmpl-1"]
            }
        };

        _projectStore.Setup(s => s.LoadProjectsAsync(_ct))
            .ReturnsAsync(existingProjects);

        PipelineProject? savedProject = null;
        _projectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), _ct))
            .Callback<PipelineProject, CancellationToken>((p, _) => savedProject = p)
            .Returns(Task.CompletedTask);

        // Act
        await ProjectMigrationService.MigrateToProjectsAsync(_projectStore.Object, _configStore.Object, _ct);

        // Assert: Self-healing recreated the Default project
        savedProject.Should().NotBeNull();
        savedProject!.Id.Should().Be(WellKnownIds.DefaultProjectId);
        savedProject.Name.Should().Be("Default");
        savedProject.TemplateIds.Should().BeEmpty();

        // Config was NOT loaded (already-migrated path, not fresh migration)
        _configStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Helpers

    private static PipelineJobTemplate CreateTemplate(string id, string name) => new()
    {
        Id = id,
        Name = name,
        IssueProviderId = $"issue-{id}",
        RepoProviderId = $"repo-{id}"
    };

    #endregion
}
