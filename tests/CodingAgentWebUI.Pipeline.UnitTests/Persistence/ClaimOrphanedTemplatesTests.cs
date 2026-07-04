using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Persistence;

public class ClaimOrphanedTemplatesTests : IDisposable
{
    private readonly string _testDir;
    private static JsonSerializerOptions JsonOptions => PipelineJsonOptions.Default;

    public ClaimOrphanedTemplatesTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ClaimOrphanedTemplatesTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public void WhenOrphanedTemplateExists_ShouldAddToDefaultProjectTemplateIds()
    {
        // Arrange: Default project with empty TemplateIds, but a template file on disk
        var templateId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: []);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, templateId);

        // Act: constructing the store triggers ClaimOrphanedTemplates
        _ = new JsonConfigurationStore(_testDir);

        // Assert
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().Contain(templateId);
    }

    [Fact]
    public void WhenTemplateAlreadyReferenced_ShouldNotDuplicate()
    {
        // Arrange: Default project already references the template
        var templateId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: [templateId]);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, templateId);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: no duplicates
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Count(id => id == templateId).Should().Be(1);
    }

    [Fact]
    public void WhenTemplateReferencedByOtherProject_ShouldNotClaimIntoDefault()
    {
        // Arrange: template is under a different project's directory AND referenced by that project
        var otherProjectId = Guid.NewGuid().ToString();
        var templateId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: []);
        SetupProject(otherProjectId, "OtherProject", templateIds: [templateId]);
        CreateTemplateFile(otherProjectId, templateId);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: Default project should NOT claim it
        var defaultProject = LoadProject(WellKnownIds.DefaultProjectId);
        defaultProject.TemplateIds.Should().BeEmpty();
    }

    [Fact]
    public void WhenMultipleOrphansExist_ShouldClaimAllIntoDefault()
    {
        // Arrange
        var templateId1 = Guid.NewGuid().ToString();
        var templateId2 = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: []);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, templateId1);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, templateId2);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().Contain(templateId1);
        project.TemplateIds.Should().Contain(templateId2);
    }

    [Fact]
    public void WhenOrphanIsUnderNonDefaultProjectDir_ShouldStillClaimIntoDefault()
    {
        // Arrange: template file is physically under another project's directory
        // but that project doesn't reference it in TemplateIds
        var otherProjectId = Guid.NewGuid().ToString();
        var templateId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: []);
        SetupProject(otherProjectId, "OtherProject", templateIds: []);
        CreateTemplateFile(otherProjectId, templateId);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: Default claims the orphan
        var defaultProject = LoadProject(WellKnownIds.DefaultProjectId);
        defaultProject.TemplateIds.Should().Contain(templateId);
    }

    [Fact]
    public void WhenNoTemplatesExist_ShouldNotModifyDefaultProject()
    {
        // Arrange
        SetupDefaultProject(templateIds: []);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().BeEmpty();
    }

    [Fact]
    public void WhenDefaultProjectHasExistingTemplateIds_ShouldPreserveThem()
    {
        // Arrange: Default project already has one template, plus an orphan
        var existingId = Guid.NewGuid().ToString();
        var orphanId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: [existingId]);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, existingId);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, orphanId);

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: existing is preserved, orphan is added
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().Contain(existingId);
        project.TemplateIds.Should().Contain(orphanId);
        project.TemplateIds.Should().HaveCount(2);
    }

    [Fact]
    public void WhenProjectFileIsMalformed_ShouldSkipItAndStillClaimOrphans()
    {
        // Arrange: one valid project (Default) and one corrupt project file
        var corruptProjectId = Guid.NewGuid().ToString();
        var orphanId = Guid.NewGuid().ToString();
        SetupDefaultProject(templateIds: []);
        CreateTemplateFile(WellKnownIds.DefaultProjectId, orphanId);

        // Write a corrupt project file
        var projectsDir = Path.Combine(_testDir, "projects");
        File.WriteAllText(Path.Combine(projectsDir, $"{corruptProjectId}.json"), "{{{{not valid json");

        // Act: should not throw, should still claim orphan
        _ = new JsonConfigurationStore(_testDir);

        // Assert
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().Contain(orphanId);
    }

    [Fact]
    public void WhenTemplateFilenameIsNotGuid_ShouldIgnoreIt()
    {
        // Arrange: Default project with a template directory containing non-GUID filenames
        SetupDefaultProject(templateIds: []);
        var templatesDir = Path.Combine(_testDir, "projects", WellKnownIds.DefaultProjectId, "templates");
        Directory.CreateDirectory(templatesDir);

        // Create files with non-GUID names
        File.WriteAllText(Path.Combine(templatesDir, "readme.json"), "{}");
        File.WriteAllText(Path.Combine(templatesDir, "backup-2024.json"), "{}");
        File.WriteAllText(Path.Combine(templatesDir, "not-a-guid.json"), "{}");

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: no orphans claimed (non-GUID filenames are ignored)
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().BeEmpty();
    }

    [Fact]
    public void WhenTemplateIdCaseDiffers_ShouldRecognizeAsReferenced()
    {
        // Arrange: template file on disk has uppercase GUID, project references lowercase
        var templateId = Guid.NewGuid().ToString();
        var upperCaseId = templateId.ToUpperInvariant();

        // Project references lowercase
        SetupDefaultProject(templateIds: [templateId]);

        // File on disk uses uppercase
        var templatesDir = Path.Combine(_testDir, "projects", WellKnownIds.DefaultProjectId, "templates");
        Directory.CreateDirectory(templatesDir);
        var template = new PipelineJobTemplate
        {
            Id = upperCaseId,
            Name = "CaseMismatch Template",
            IssueProviderId = "test",
            RepoProviderId = "test"
        };
        File.WriteAllText(
            Path.Combine(templatesDir, $"{upperCaseId}.json"),
            JsonSerializer.Serialize(template, JsonOptions));

        // Act
        _ = new JsonConfigurationStore(_testDir);

        // Assert: NOT treated as orphan due to case-insensitive comparison
        var project = LoadProject(WellKnownIds.DefaultProjectId);
        project.TemplateIds.Should().HaveCount(1); // original reference only, no duplicate
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupDefaultProject(IReadOnlyList<string> templateIds)
    {
        SetupProject(WellKnownIds.DefaultProjectId, "Default", templateIds);
    }

    private void SetupProject(string projectId, string name, IReadOnlyList<string> templateIds)
    {
        var projectsDir = Path.Combine(_testDir, "projects");
        Directory.CreateDirectory(projectsDir);

        var project = new PipelineProject
        {
            Id = projectId,
            Name = name,
            Enabled = true,
            TemplateIds = templateIds
        };

        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(Path.Combine(projectsDir, $"{projectId}.json"), json);
    }

    private void CreateTemplateFile(string projectId, string templateId)
    {
        var templatesDir = Path.Combine(_testDir, "projects", projectId, "templates");
        Directory.CreateDirectory(templatesDir);

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = $"Test Template {templateId[..8]}",
            IssueProviderId = "test-issue-provider",
            RepoProviderId = "test-repo-provider"
        };

        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(Path.Combine(templatesDir, $"{templateId}.json"), json);
    }

    private PipelineProject LoadProject(string projectId)
    {
        var path = Path.Combine(_testDir, "projects", $"{projectId}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PipelineProject>(json, JsonOptions)!;
    }
}
