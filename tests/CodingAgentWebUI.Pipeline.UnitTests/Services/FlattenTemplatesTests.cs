// Feature: 029-pipeline-projects
// Unit tests for PipelineLoopService.FlattenTemplates
// Tests: alphabetical project ordering, template ordering within project,
//        disabled project skipping, missing template ID handling.
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the FlattenTemplates method on PipelineLoopService.
/// Validates: Requirements 8.1, 8.2, 8.3, 8.4
/// </summary>
public class FlattenTemplatesTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineOrchestrationService _orchestration;
    private PipelineLoopService? _loopService;

    public FlattenTemplatesTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockLogger = new Mock<Serilog.ILogger>();

        // Allow logger.Warning(...) calls without strict setup
        _mockLogger.Setup(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()));
        _mockLogger.Setup(l => l.Warning(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<object>()));
        _mockLogger.Setup(l => l.Warning(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()));

        var mockValidator = new Mock<IQualityGateValidator>();
        _orchestration = new PipelineOrchestrationService(
            _mockStore.Object, _mockFactory.Object, new IssueDescriptionParser(),
            new AgentPhaseExecutor(_mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(_mockLogger.Object), new CiLogWriter(_mockLogger.Object), new FeedbackService(_mockLogger.Object), _mockLogger.Object),
            _mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);
    }

    private PipelineLoopService CreateService()
    {
        _loopService = new PipelineLoopService(
            _orchestration, _mockFactory.Object,
            _mockStore.Object, _mockStore.Object, _mockStore.Object,
            _mockLogger.Object);
        return _loopService;
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); } catch { }
            _loopService.Dispose();
        }
    }

    // ── Requirement 8.1: Projects ordered alphabetically by Name ──────────────

    /// <summary>
    /// Validates: Requirement 8.1 — projects ordered alphabetically by Name.
    /// Two projects "Alpha" and "Beta" — templates should come out ordered
    /// by project name alphabetically.
    /// </summary>
    [Fact]
    public void FlattenTemplates_OrdersProjectsAlphabeticallyByName()
    {
        // Arrange
        var templateA = new PipelineJobTemplate
        {
            Id = "tmpl-a", Name = "Template A",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var templateB = new PipelineJobTemplate
        {
            Id = "tmpl-b", Name = "Template B",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        var projectBeta = TestPipelineConfig.WithProject("Beta", "tmpl-b");
        var projectAlpha = TestPipelineConfig.WithProject("Alpha", "tmpl-a");

        // Put Beta first in the list to verify ordering is applied
        var projects = new List<PipelineProject> { projectBeta, projectAlpha };

        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { templateA, templateB }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — Alpha's template should come before Beta's
        Assert.Equal(2, result.Count);
        Assert.Equal("tmpl-a", result[0].Template.Id);
        Assert.Equal("Alpha", result[0].Project.Name);
        Assert.Equal("tmpl-b", result[1].Template.Id);
        Assert.Equal("Beta", result[1].Project.Name);
    }

    /// <summary>
    /// Validates: Requirement 8.1 — alphabetical ordering uses ordinal comparison.
    /// Verifies that uppercase/lowercase ordering follows StringComparer.Ordinal semantics.
    /// </summary>
    [Fact]
    public void FlattenTemplates_UsesOrdinalStringComparison()
    {
        // Arrange: Ordinal comparison puts uppercase before lowercase
        var templateA = new PipelineJobTemplate
        {
            Id = "tmpl-a", Name = "Template A",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var templateB = new PipelineJobTemplate
        {
            Id = "tmpl-b", Name = "Template B",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        // "Apple" sorts before "banana" in Ordinal (uppercase A < lowercase b)
        var projectBanana = TestPipelineConfig.WithProject("banana", "tmpl-b");
        var projectApple = TestPipelineConfig.WithProject("Apple", "tmpl-a");

        var projects = new List<PipelineProject> { projectBanana, projectApple };

        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { templateA, templateB }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — "Apple" (uppercase A=65) sorts before "banana" (lowercase b=98)
        Assert.Equal(2, result.Count);
        Assert.Equal("Apple", result[0].Project.Name);
        Assert.Equal("banana", result[1].Project.Name);
    }

    // ── Requirement 8.2: Templates ordered by TemplateIds position within project ──

    /// <summary>
    /// Validates: Requirement 8.2 — templates within a project are ordered
    /// by their position in the project's TemplateIds list.
    /// </summary>
    [Fact]
    public void FlattenTemplates_PreservesTemplateOrderWithinProject()
    {
        // Arrange
        var template1 = new PipelineJobTemplate
        {
            Id = "tmpl-1", Name = "First",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var template2 = new PipelineJobTemplate
        {
            Id = "tmpl-2", Name = "Second",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var template3 = new PipelineJobTemplate
        {
            Id = "tmpl-3", Name = "Third",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        // TemplateIds order: 3, 1, 2
        var project = TestPipelineConfig.WithProject("MyProject", "tmpl-3", "tmpl-1", "tmpl-2");

        var projects = new List<PipelineProject> { project };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { template1, template2, template3 }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — templates come out in TemplateIds position order: 3, 1, 2
        Assert.Equal(3, result.Count);
        Assert.Equal("tmpl-3", result[0].Template.Id);
        Assert.Equal("tmpl-1", result[1].Template.Id);
        Assert.Equal("tmpl-2", result[2].Template.Id);
    }

    /// <summary>
    /// Validates: Requirements 8.1, 8.2 — combined ordering: projects alphabetical,
    /// then templates by TemplateIds position within each project.
    /// </summary>
    [Fact]
    public void FlattenTemplates_CombinesProjectAlphabeticalAndTemplatePositionOrder()
    {
        // Arrange
        var templateA1 = new PipelineJobTemplate
        {
            Id = "tmpl-a1", Name = "Alpha T1",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var templateA2 = new PipelineJobTemplate
        {
            Id = "tmpl-a2", Name = "Alpha T2",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var templateB1 = new PipelineJobTemplate
        {
            Id = "tmpl-b1", Name = "Beta T1",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        // Alpha has templates in order: a2, a1 — Beta has: b1
        var projectAlpha = TestPipelineConfig.WithProject("Alpha", "tmpl-a2", "tmpl-a1");
        var projectBeta = TestPipelineConfig.WithProject("Beta", "tmpl-b1");

        var projects = new List<PipelineProject> { projectBeta, projectAlpha };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { templateA1, templateA2, templateB1 }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — Alpha first (alphabetical), then Beta. Within Alpha: a2, a1 (position order)
        Assert.Equal(3, result.Count);
        Assert.Equal("tmpl-a2", result[0].Template.Id);
        Assert.Equal("tmpl-a1", result[1].Template.Id);
        Assert.Equal("tmpl-b1", result[2].Template.Id);
    }

    // ── Requirement 8.3: Disabled project skipping ──────────────

    /// <summary>
    /// Validates: Requirement 8.3 — disabled projects are skipped entirely,
    /// regardless of individual template Enabled flags.
    /// </summary>
    [Fact]
    public void FlattenTemplates_SkipsDisabledProjects()
    {
        // Arrange
        var template1 = new PipelineJobTemplate
        {
            Id = "tmpl-1", Name = "Enabled Template",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var template2 = new PipelineJobTemplate
        {
            Id = "tmpl-2", Name = "Also Enabled",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        var enabledProject = TestPipelineConfig.WithProject("Alpha", "tmpl-1");
        var disabledProject = TestPipelineConfig.WithProject("Beta", "tmpl-2") with { Enabled = false };

        var projects = new List<PipelineProject> { enabledProject, disabledProject };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { template1, template2 }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — only Alpha's template appears (Beta is disabled)
        Assert.Single(result);
        Assert.Equal("tmpl-1", result[0].Template.Id);
        Assert.Equal("Alpha", result[0].Project.Name);
    }

    /// <summary>
    /// Validates: Requirement 8.3 — a disabled project's templates are excluded entirely
    /// even if the templates themselves are enabled.
    /// </summary>
    [Fact]
    public void FlattenTemplates_DisabledProjectExcludesAllItsTemplates()
    {
        // Arrange: project has multiple enabled templates but project itself is disabled
        var template1 = new PipelineJobTemplate
        {
            Id = "tmpl-1", Name = "T1",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var template2 = new PipelineJobTemplate
        {
            Id = "tmpl-2", Name = "T2",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        var disabledProject = TestPipelineConfig.WithProject("DisabledProject", "tmpl-1", "tmpl-2")
            with { Enabled = false };

        var projects = new List<PipelineProject> { disabledProject };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { template1, template2 }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — no templates in result
        Assert.Empty(result);
    }

    /// <summary>
    /// Validates: Requirement 8.4 (template-level Enabled flag) — within an enabled project,
    /// individually disabled templates are still skipped.
    /// </summary>
    [Fact]
    public void FlattenTemplates_SkipsDisabledTemplatesWithinEnabledProject()
    {
        // Arrange
        var enabledTemplate = new PipelineJobTemplate
        {
            Id = "tmpl-1", Name = "Enabled",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };
        var disabledTemplate = new PipelineJobTemplate
        {
            Id = "tmpl-2", Name = "Disabled",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false
        };

        var project = TestPipelineConfig.WithProject("MyProject", "tmpl-1", "tmpl-2");

        var projects = new List<PipelineProject> { project };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { enabledTemplate, disabledTemplate }
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert — only the enabled template appears
        Assert.Single(result);
        Assert.Equal("tmpl-1", result[0].Template.Id);
    }

    // ── Requirement 8.4: Missing template ID handling ──────────────

    /// <summary>
    /// Validates: Requirement 8.4 — a template ID that doesn't exist in
    /// PipelineConfiguration is skipped (not crash) with a warning.
    /// </summary>
    [Fact]
    public void FlattenTemplates_SkipsMissingTemplateIds()
    {
        // Arrange
        var existingTemplate = new PipelineJobTemplate
        {
            Id = "tmpl-exists", Name = "Exists",
            IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true
        };

        // Project references a template that doesn't exist ("tmpl-missing")
        var project = TestPipelineConfig.WithProject("MyProject", "tmpl-exists", "tmpl-missing");

        var projects = new List<PipelineProject> { project };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate> { existingTemplate }
        };

        var svc = CreateService();

        // Act — should not throw
        var result = svc.FlattenTemplates(projects, config);

        // Assert — only the existing template appears
        Assert.Single(result);
        Assert.Equal("tmpl-exists", result[0].Template.Id);
    }

    /// <summary>
    /// Validates: Requirement 8.4 — when all template IDs in a project are missing,
    /// the project produces no entries in the result (empty output, not crash).
    /// </summary>
    [Fact]
    public void FlattenTemplates_AllMissingTemplateIds_ProducesEmptyResult()
    {
        // Arrange: project references templates that don't exist
        var project = TestPipelineConfig.WithProject("GhostProject", "tmpl-ghost1", "tmpl-ghost2");

        var projects = new List<PipelineProject> { project };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate>()
        };

        var svc = CreateService();

        // Act
        var result = svc.FlattenTemplates(projects, config);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Validates: Requirement 8.4 — missing template ID logs a warning.
    /// </summary>
    [Fact]
    public void FlattenTemplates_MissingTemplateId_LogsWarning()
    {
        // Arrange
        var project = TestPipelineConfig.WithProject("MyProject", "tmpl-missing");
        var projects = new List<PipelineProject> { project };
        var config = TestPipelineConfig.Default() with
        {
            PipelineJobTemplates = new List<PipelineJobTemplate>()
        };

        var svc = CreateService();

        // Act
        svc.FlattenTemplates(projects, config);

        // Assert — logger.Warning was called with appropriate parameters
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(msg => msg.Contains("does not exist")),
                It.Is<string>(v => v == "MyProject"),
                It.Is<string>(v => v == "tmpl-missing")),
            Times.Once);
    }
}
