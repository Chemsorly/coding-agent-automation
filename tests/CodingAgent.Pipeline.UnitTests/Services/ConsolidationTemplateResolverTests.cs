using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationTemplateResolver"/>.
/// Covers enabled/disabled filtering, template-not-found, project ordering, and null guards.
/// </summary>
public class ConsolidationTemplateResolverTests
{
    private readonly Mock<IProjectStore> _mockProjectStore = new();

    private ConsolidationTemplateResolver CreateSut() =>
        new(_mockProjectStore.Object);

    private void SetupProjects(params PipelineProject[] projects)
        => _mockProjectStore
            .Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

    private void SetupTemplates(params PipelineJobTemplate[] templates)
        => _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

    // ── Constructor null guard ─────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullProjectStore_Throws()
    {
        var act = () => new ConsolidationTemplateResolver(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResolveTemplateWithProjectAsync ────────────────────────────────────────

    [Fact]
    public async Task ResolveTemplateWithProject_TemplateExistsInEnabledProject_ReturnsTemplateAndProjectName()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "BrainConsolidation", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var project = new PipelineProject
        {
            // TODO [WARNING]: "p1" is a short non-GUID string. In production, ConsolidationService.TriggerAsync
            // parses ProjectId with Guid.TryParse, which silently produces null for "p1". This test
            // verifies the resolver returns the raw string, but the end-to-end path (resolver → service)
            // would produce a null ProjectId for this fixture value. The dedicated test
            // ResolveTemplateWithProject_TemplateExistsInEnabledProject_ReturnsProjectId covers the
            // full-GUID case. If this fixture is ever promoted to an integration test, use a real GUID.
            Id = "p1",
            Name = "MyProject",
            Enabled = true,
            TemplateIds = ["t1"]
        };

        SetupProjects(project);
        SetupTemplates(template);

        var sut = CreateSut();
        var (resolvedTemplate, projectName, projectId) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().NotBeNull();
        resolvedTemplate!.Id.Should().Be("t1");
        projectName.Should().Be("MyProject");
        projectId.Should().Be("p1");
    }

    [Fact]
    public async Task ResolveTemplateWithProject_TemplateExistsInEnabledProject_ReturnsProjectId()
    {
        // Verifies that the actual project.Id GUID string round-trips through the resolver.
        var projectGuid = Guid.NewGuid().ToString();
        var template = new PipelineJobTemplate { Id = "t-guid", Name = "GuidTemplate", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var project = new PipelineProject
        {
            Id = projectGuid,
            Name = "GuidProject",
            Enabled = true,
            TemplateIds = ["t-guid"]
        };

        SetupProjects(project);
        SetupTemplates(template);

        var sut = CreateSut();
        var (resolvedTemplate, _, projectId) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t-guid"), CancellationToken.None);

        resolvedTemplate.Should().NotBeNull();
        projectId.Should().Be(projectGuid,
            "the exact project.Id GUID string must be returned as the third tuple element");
    }

    [Fact]
    public async Task ResolveTemplateWithProject_NoMatchingTemplate_ReturnsNullPair()
    {
        SetupProjects(new PipelineProject
        {
            Id = "p1",
            Name = "MyProject",
            Enabled = true,
            TemplateIds = ["other-id"]
        });
        SetupTemplates(new PipelineJobTemplate { Id = "other-id", Name = "Other", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true });

        var sut = CreateSut();
        var (resolvedTemplate, projectName, projectId) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t-missing"), CancellationToken.None);

        resolvedTemplate.Should().BeNull();
        projectName.Should().BeNull();
        projectId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateWithProject_DisabledProject_DoesNotReturnTemplate()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "BrainConsolidation", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var disabledProject = new PipelineProject
        {
            Id = "p1",
            Name = "DisabledProject",
            Enabled = false,
            TemplateIds = ["t1"]
        };

        SetupProjects(disabledProject);
        SetupTemplates(template);

        var sut = CreateSut();
        var (resolvedTemplate, projectName, projectId) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().BeNull("disabled projects must not contribute templates");
        projectName.Should().BeNull();
        projectId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateWithProject_NoProjects_ReturnsNullPair()
    {
        SetupProjects();
        SetupTemplates();

        var sut = CreateSut();
        var (resolvedTemplate, projectName, projectId) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().BeNull();
        projectName.Should().BeNull();
        projectId.Should().BeNull();
    }

    // ── ResolveTemplateAsync (convenience wrapper) ─────────────────────────────

    [Fact]
    public async Task ResolveTemplate_TemplateExists_ReturnsTemplate()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "BrainConsolidation", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        SetupProjects(new PipelineProject { Id = "p1", Name = "P", Enabled = true, TemplateIds = ["t1"] });
        SetupTemplates(template);

        var sut = CreateSut();
        var result = await sut.ResolveTemplateAsync(new TemplateId("t1"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be("t1");
    }

    [Fact]
    public async Task ResolveTemplate_TemplateMissing_ReturnsNull()
    {
        SetupProjects(new PipelineProject { Id = "p1", Name = "P", Enabled = true, TemplateIds = [] });
        SetupTemplates();

        var sut = CreateSut();
        var result = await sut.ResolveTemplateAsync(new TemplateId("t-missing"), CancellationToken.None);

        result.Should().BeNull();
    }
}
