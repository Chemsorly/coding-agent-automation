using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests.Persistence;

/// <summary>
/// Tests for template management via IProjectStore using InMemoryConfigurationStore.
/// Migrated from JsonConfigurationStore by Spec 041. The original tests exercised
/// JsonConfigurationStore's constructor-time orphan-claiming (file-system-specific behavior).
/// Tests now verify the IProjectStore template CRUD contract.
/// </summary>
public class ClaimOrphanedTemplatesTests
{
    private static InMemoryConfigurationStore CreateStore()
    {
        var store = new InMemoryConfigurationStore();
        // Ensure Default project exists
        store.SaveProjectAsync(new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None).GetAwaiter().GetResult();
        return store;
    }

    [Fact]
    public async Task SaveTemplate_AddsToProjectTemplateIds()
    {
        var store = CreateStore();
        var templateId = Guid.NewGuid().ToString();
        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Test Template",
            IssueProviderId = "test-issue-provider",
            RepoProviderId = "test-repo-provider"
        };

        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId, template, CancellationToken.None);

        var project = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        project!.TemplateIds.Should().Contain(templateId);
    }

    [Fact]
    public async Task SaveTemplate_IdempotentSave_DoesNotDuplicateTemplateId()
    {
        var store = CreateStore();
        var templateId = Guid.NewGuid().ToString();
        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Test Template",
            IssueProviderId = "ip",
            RepoProviderId = "rp"
        };

        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId, template, CancellationToken.None);
        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId, template, CancellationToken.None);

        var project = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        project!.TemplateIds.Count(id => id == templateId).Should().Be(1);
    }

    [Fact]
    public async Task SaveTemplateToOtherProject_DoesNotAffectDefaultProjectTemplateIds()
    {
        var store = CreateStore();
        var otherProjectId = Guid.NewGuid().ToString();
        var templateId = Guid.NewGuid().ToString();
        await store.SaveProjectAsync(new PipelineProject { Id = otherProjectId, Name = "Other" }, CancellationToken.None);

        var template = new PipelineJobTemplate
        {
            Id = templateId, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp"
        };
        await store.SaveTemplateAsync(otherProjectId, template, CancellationToken.None);

        var defaultProject = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        defaultProject!.TemplateIds.Should().NotContain(templateId);
    }

    [Fact]
    public async Task SaveMultipleTemplates_AllAddedToProjectTemplateIds()
    {
        var store = CreateStore();
        var templateId1 = Guid.NewGuid().ToString();
        var templateId2 = Guid.NewGuid().ToString();

        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = templateId1, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);
        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = templateId2, Name = "T2", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);

        var project = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        project!.TemplateIds.Should().Contain(templateId1);
        project.TemplateIds.Should().Contain(templateId2);
    }

    [Fact]
    public async Task SaveTemplate_WithExistingTemplates_PreservesExistingIds()
    {
        var store = CreateStore();
        var existingId = Guid.NewGuid().ToString();
        var newId = Guid.NewGuid().ToString();

        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = existingId, Name = "Existing", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);
        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = newId, Name = "New", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);

        var project = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        project!.TemplateIds.Should().Contain(existingId);
        project.TemplateIds.Should().Contain(newId);
        project.TemplateIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteTemplate_RemovesFromProjectTemplateIds()
    {
        var store = CreateStore();
        var templateId = Guid.NewGuid().ToString();
        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = templateId, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);

        await store.DeleteTemplateAsync(WellKnownIds.DefaultProjectId, templateId, CancellationToken.None);

        var project = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        project!.TemplateIds.Should().NotContain(templateId);
    }

    [Fact]
    public async Task LoadTemplatesForProject_EmptyProject_ReturnsEmpty()
    {
        var store = CreateStore();

        var templates = await store.LoadTemplatesForProjectAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);

        templates.Should().BeEmpty();
    }

    [Fact]
    public async Task MoveTemplate_FromDefaultToOther_UpdatesBothProjectIds()
    {
        var store = CreateStore();
        var otherProjectId = Guid.NewGuid().ToString();
        await store.SaveProjectAsync(new PipelineProject { Id = otherProjectId, Name = "Other" }, CancellationToken.None);

        var templateId = Guid.NewGuid().ToString();
        await store.SaveTemplateAsync(WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate { Id = templateId, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
            CancellationToken.None);

        await store.MoveTemplateAsync(WellKnownIds.DefaultProjectId, otherProjectId, templateId, CancellationToken.None);

        var defaultProject = await store.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None);
        defaultProject!.TemplateIds.Should().NotContain(templateId);

        var otherProject = await store.GetProjectByIdAsync(otherProjectId, CancellationToken.None);
        otherProject!.TemplateIds.Should().Contain(templateId);
    }
}
