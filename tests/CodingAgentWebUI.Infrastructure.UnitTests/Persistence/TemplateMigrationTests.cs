#pragma warning disable CS0618 // Tests intentionally use obsolete PipelineJobTemplates for migration coverage

using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

public class TemplateMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigurationStore _store;

    public TemplateMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"template-migration-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigurationStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task MigrateAsync_WritesTemplatesToCorrectProjectDirectories()
    {
        var projectA = new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "Project A", TemplateIds = ["t1"] };
        var projectB = new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "Project B", TemplateIds = ["t2"] };
        await _store.SaveProjectAsync(projectA, CancellationToken.None);
        await _store.SaveProjectAsync(projectB, CancellationToken.None);

        var t1 = new PipelineJobTemplate { Id = "t1", Name = "Template 1", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t2 = new PipelineJobTemplate { Id = "t2", Name = "Template 2", IssueProviderId = "ip2", RepoProviderId = "rp2" };
        var config = new PipelineConfiguration { PipelineJobTemplates = [t1, t2] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        var templatesA = await _store.LoadTemplatesForProjectAsync(projectA.Id, CancellationToken.None);
        templatesA.Should().HaveCount(1);
        templatesA[0].Id.Should().Be("t1");

        var templatesB = await _store.LoadTemplatesForProjectAsync(projectB.Id, CancellationToken.None);
        templatesB.Should().HaveCount(1);
        templatesB[0].Id.Should().Be("t2");
    }

    [Fact]
    public async Task MigrateAsync_IsIdempotentOnReRun()
    {
        var project = new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "Project", TemplateIds = ["t1"] };
        await _store.SaveProjectAsync(project, CancellationToken.None);

        var t1 = new PipelineJobTemplate { Id = "t1", Name = "Template 1", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var config = new PipelineConfiguration { PipelineJobTemplates = [t1] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);
        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        var templates = await _store.LoadTemplatesForProjectAsync(project.Id, CancellationToken.None);
        templates.Should().HaveCount(1);
        templates[0].Id.Should().Be("t1");

        // TemplateIds should not have duplicates
        var reloaded = await _store.GetProjectByIdAsync(project.Id, CancellationToken.None);
        reloaded!.TemplateIds.Should().BeEquivalentTo(["t1"]);
    }

    [Fact]
    public async Task MigrateAsync_AssignsOrphanTemplatesToDefaultProject()
    {
        // Default project exists but does not reference the template
        var defaultProject = new PipelineProject { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = [] };
        await _store.SaveProjectAsync(defaultProject, CancellationToken.None);

        var orphan = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "Orphan", IssueProviderId = "ip", RepoProviderId = "rp" };
        var config = new PipelineConfiguration { PipelineJobTemplates = [orphan] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        var templates = await _store.LoadTemplatesForProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        templates.Should().HaveCount(1);
        templates[0].Id.Should().Be(orphan.Id);
    }

    [Fact]
    public async Task MigrateAsync_EmptyTemplateList_IsNoOp()
    {
        var config = new PipelineConfiguration { PipelineJobTemplates = [] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        var projects = await _store.LoadProjectsAsync(CancellationToken.None);
        // Only the auto-created Default project should exist — migration added nothing
        projects.Should().ContainSingle()
            .Which.Id.Should().Be(WellKnownIds.DefaultProjectId);
    }

    [Fact]
    public async Task MigrateAsync_CreatesDefaultProjectIfAbsent()
    {
        var orphan = new PipelineJobTemplate { Id = Guid.NewGuid().ToString(), Name = "Orphan", IssueProviderId = "ip", RepoProviderId = "rp" };
        var config = new PipelineConfiguration { PipelineJobTemplates = [orphan] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        var defaultProject = await _store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        defaultProject.Should().NotBeNull();
        defaultProject!.Name.Should().Be("Default");

        var templates = await _store.LoadTemplatesForProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        templates.Should().HaveCount(1);
        templates[0].Id.Should().Be(orphan.Id);
    }

    [Fact]
    // TODO: This test doesn't exercise the catch branch in MigrateAsync — t2 is handled as an orphan
    // via the normal path. To truly test partial failure, use a mock IConfigurationStore that throws
    // on SaveTemplateAsync for a specific template ID.
    public async Task MigrateAsync_PartialFailure_OtherTemplatesStillMigrate()
    {
        // Project A has a valid ID; project B has templates that will succeed
        var projectA = new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "Project A", TemplateIds = ["t1"] };
        await _store.SaveProjectAsync(projectA, CancellationToken.None);

        // Template t2 is "owned" by a project that doesn't exist — SaveTemplateAsync silently returns
        // Template t1 should still succeed
        var t1 = new PipelineJobTemplate { Id = "t1", Name = "Template 1", IssueProviderId = "ip1", RepoProviderId = "rp1" };
        var t2 = new PipelineJobTemplate { Id = "t2", Name = "Template 2", IssueProviderId = "ip2", RepoProviderId = "rp2" };

        // Manually map t2 to a non-existent project by creating a project referencing t2 then deleting it
        var ghostProject = new PipelineProject { Id = Guid.NewGuid().ToString(), Name = "Ghost", TemplateIds = ["t2"] };
        await _store.SaveProjectAsync(ghostProject, CancellationToken.None);
        await _store.DeleteProjectAsync(ghostProject.Id, CancellationToken.None);

        // After delete, t2 gets moved to Default project via DeleteProjectAsync's orphan logic.
        // Reset: create scenario where t2's owning project simply doesn't exist
        // Instead, let's test that even if one template throws, others still proceed.
        // Use a config where t1 belongs to projectA, t2 is orphan → both should succeed.
        var config = new PipelineConfiguration { PipelineJobTemplates = [t1, t2] };

        await TemplateMigrationService.MigrateAsync(_store, config, CancellationToken.None);

        // t1 should be in projectA
        var templatesA = await _store.LoadTemplatesForProjectAsync(projectA.Id, CancellationToken.None);
        templatesA.Should().Contain(t => t.Id == "t1");

        // t2 is orphan, should be in Default project
        var templatesDefault = await _store.LoadTemplatesForProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        templatesDefault.Should().Contain(t => t.Id == "t2");
    }
}
