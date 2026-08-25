using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
            Id = "p1", Name = "MyProject", Enabled = true,
            TemplateIds = ["t1"]
        };

        SetupProjects(project);
        SetupTemplates(template);

        var sut = CreateSut();
        var (resolvedTemplate, projectName) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().NotBeNull();
        resolvedTemplate!.Id.Should().Be("t1");
        projectName.Should().Be("MyProject");
    }

    [Fact]
    public async Task ResolveTemplateWithProject_NoMatchingTemplate_ReturnsNullPair()
    {
        SetupProjects(new PipelineProject
        {
            Id = "p1", Name = "MyProject", Enabled = true,
            TemplateIds = ["other-id"]
        });
        SetupTemplates(new PipelineJobTemplate { Id = "other-id", Name = "Other", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true });

        var sut = CreateSut();
        var (resolvedTemplate, projectName) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t-missing"), CancellationToken.None);

        resolvedTemplate.Should().BeNull();
        projectName.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateWithProject_DisabledProject_DoesNotReturnTemplate()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "BrainConsolidation", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var disabledProject = new PipelineProject
        {
            Id = "p1", Name = "DisabledProject", Enabled = false,
            TemplateIds = ["t1"]
        };

        SetupProjects(disabledProject);
        SetupTemplates(template);

        var sut = CreateSut();
        var (resolvedTemplate, projectName) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().BeNull("disabled projects must not contribute templates");
        projectName.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateWithProject_NoProjects_ReturnsNullPair()
    {
        SetupProjects();
        SetupTemplates();

        var sut = CreateSut();
        var (resolvedTemplate, projectName) =
            await sut.ResolveTemplateWithProjectAsync(new TemplateId("t1"), CancellationToken.None);

        resolvedTemplate.Should().BeNull();
        projectName.Should().BeNull();
    }

    // ── GetEnabledTemplatesFromProjectsAsync ───────────────────────────────────

    [Fact]
    public async Task GetEnabledTemplates_ReturnsOnlyEnabledTemplates()
    {
        var enabled = new PipelineJobTemplate { Id = "t-enabled", Name = "Enabled", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var disabled = new PipelineJobTemplate { Id = "t-disabled", Name = "Disabled", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false };
        var project = new PipelineProject
        {
            Id = "p1", Name = "P1", Enabled = true,
            TemplateIds = ["t-enabled", "t-disabled"]
        };

        SetupProjects(project);
        SetupTemplates(enabled, disabled);

        var sut = CreateSut();
        var result = await sut.GetEnabledTemplatesFromProjectsAsync(CancellationToken.None);

        result.Should().ContainSingle(t => t.Id == "t-enabled");
        result.Should().NotContain(t => t.Id == "t-disabled",
            "disabled templates must be excluded regardless of project state");
    }

    [Fact]
    public async Task GetEnabledTemplates_ExcludesDisabledProjects()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "T1", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var disabledProject = new PipelineProject
        {
            Id = "p1", Name = "Disabled", Enabled = false,
            TemplateIds = ["t1"]
        };

        SetupProjects(disabledProject);
        SetupTemplates(template);

        var sut = CreateSut();
        var result = await sut.GetEnabledTemplatesFromProjectsAsync(CancellationToken.None);

        result.Should().BeEmpty("disabled projects must not contribute any templates");
    }

    [Fact]
    public async Task GetEnabledTemplates_OrderedByProjectNameAscending()
    {
        var tA = new PipelineJobTemplate { Id = "t-alpha", Name = "TAlpha", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var tB = new PipelineJobTemplate { Id = "t-beta", Name = "TBeta", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };
        var tZ = new PipelineJobTemplate { Id = "t-zeta", Name = "TZeta", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true };

        var projectAlpha = new PipelineProject { Id = "pA", Name = "Alpha",  Enabled = true, TemplateIds = ["t-alpha"] };
        var projectZeta  = new PipelineProject { Id = "pZ", Name = "Zeta",   Enabled = true, TemplateIds = ["t-zeta"]  };
        var projectBeta  = new PipelineProject { Id = "pB", Name = "Beta",   Enabled = true, TemplateIds = ["t-beta"]  };

        // Intentionally out of order — resolver must order by project name
        SetupProjects(projectZeta, projectAlpha, projectBeta);
        SetupTemplates(tA, tB, tZ);

        var sut = CreateSut();
        var result = await sut.GetEnabledTemplatesFromProjectsAsync(CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("t-alpha", "Alpha project comes first alphabetically");
        result[1].Id.Should().Be("t-beta",  "Beta project comes second");
        result[2].Id.Should().Be("t-zeta",  "Zeta project comes last");
    }

    [Fact]
    public async Task GetEnabledTemplates_NoProjects_ReturnsEmpty()
    {
        SetupProjects();
        SetupTemplates();

        var sut = CreateSut();
        var result = await sut.GetEnabledTemplatesFromProjectsAsync(CancellationToken.None);

        result.Should().BeEmpty();
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
