using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="CreateSubIssuesStep"/> cross-repo routing logic.
/// Tests the <c>ResolveTargetIssueProviderId</c> method directly (pure routing logic)
/// and verifies labels are applied regardless of routing path.
/// Feature: 029-pipeline-projects, Requirements: 7.3, 7.4, 7.5, 7.6
/// </summary>
public class CreateSubIssuesStepRoutingTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public CreateSubIssuesStepRoutingTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"routing-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    #region ResolveTargetIssueProviderId — target resolved → correct provider used (Req 7.3)

    [Fact]
    public void ResolveTargetIssueProviderId_TargetMatchesTemplate_ReturnsTemplateIssueProviderId()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "CrossRepoProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "Backend REST API",
                    DecompositionEnabled = true,
                    Available = true,
                    IssueProviderId = "provider-backend-001"
                },
                new RepositoryTarget
                {
                    TemplateName = "frontend-web",
                    Description = "React web frontend",
                    DecompositionEnabled = true,
                    Available = true,
                    IssueProviderId = "provider-frontend-002"
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "backend-api", context, _logger);

        // Assert
        result.Should().Be("provider-backend-001");
    }

    [Fact]
    public void ResolveTargetIssueProviderId_TargetMatchesSecondTemplate_ReturnsCorrectProviderId()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "MultiRepo",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "service-a",
                    Description = "Service A",
                    IssueProviderId = "provider-a"
                },
                new RepositoryTarget
                {
                    TemplateName = "service-b",
                    Description = "Service B",
                    IssueProviderId = "provider-b"
                },
                new RepositoryTarget
                {
                    TemplateName = "shared-lib",
                    Description = "Shared library",
                    IssueProviderId = "provider-shared"
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "shared-lib", context, _logger);

        // Assert
        result.Should().Be("provider-shared");
    }

    [Fact]
    public void ResolveTargetIssueProviderId_MatchIsCaseSensitive_ExactMatchReturnsProvider()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "CaseTest",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "Backend-API",
                    Description = "API",
                    IssueProviderId = "provider-case"
                }
            ]
        };

        // Act — exact match
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "Backend-API", context, _logger);

        // Assert
        result.Should().Be("provider-case");
    }

    #endregion

    #region ResolveTargetIssueProviderId — target unresolved → fallback (Req 7.4)

    [Fact]
    public void ResolveTargetIssueProviderId_TargetDoesNotMatchAnyTemplate_ReturnsNull()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "API",
                    IssueProviderId = "provider-001"
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "nonexistent-repo", context, _logger);

        // Assert — null means fallback to dispatching template's provider
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_CaseMismatch_ReturnsNull()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "CaseTest",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "API",
                    IssueProviderId = "provider-001"
                }
            ]
        };

        // Act — "Backend-API" ≠ "backend-api" (case-sensitive Ordinal comparison)
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "Backend-API", context, _logger);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_TargetMatchesButNoIssueProviderId_ReturnsNull()
    {
        // Arrange — template exists but has no IssueProviderId configured
        var context = new DecompositionProjectContext
        {
            ProjectName = "MissingProvider",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "docs-repo",
                    Description = "Documentation",
                    DecompositionEnabled = true,
                    Available = true,
                    IssueProviderId = null
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "docs-repo", context, _logger);

        // Assert — null IssueProviderId means fallback to dispatching template
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_TargetMatchesButEmptyIssueProviderId_ReturnsNull()
    {
        // Arrange — template exists but IssueProviderId is empty string
        var context = new DecompositionProjectContext
        {
            ProjectName = "EmptyProvider",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "infra-repo",
                    Description = "Infrastructure",
                    IssueProviderId = ""
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "infra-repo", context, _logger);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ResolveTargetIssueProviderId — target null → default behavior (Req 7.5)

    [Fact]
    public void ResolveTargetIssueProviderId_NullTarget_ReturnsNull()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "API",
                    IssueProviderId = "provider-001"
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            null, context, _logger);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_EmptyTarget_ReturnsNull()
    {
        // Arrange
        var context = new DecompositionProjectContext
        {
            ProjectName = "TestProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "API",
                    IssueProviderId = "provider-001"
                }
            ]
        };

        // Act
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "", context, _logger);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_NullProjectContext_ReturnsNull()
    {
        // Act — no project context (per-template decomposition, backward compatible)
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            "some-repo", projectContext: null, logger: _logger);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ResolveTargetIssueProviderId_NullTargetAndNullContext_ReturnsNull()
    {
        // Act — both null (edge case)
        var result = CreateSubIssuesStep.ResolveTargetIssueProviderId(
            null, projectContext: null, logger: null);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Labels applied regardless of routing (Req 7.6)

    [Fact]
    public async Task ExecuteAsync_DefaultRouting_AppliesAgentNextAndGeneratedLabels()
    {
        // Arrange — write sub-issue without targetRepository (default routing path)
        WriteSubIssueFile("01-feature.json", "Add logging", "Logging module");

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "102", Url = "https://example.com/102" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — agent:next and agent:generated labels applied in default routing path
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
    }

    [Fact]
    public async Task ExecuteAsync_WithProjectContext_DefaultRouting_StillAppliesLabels()
    {
        // Arrange — sub-issue without targetRepository, but project context is present
        // (verifies labels are applied when routing falls back to default even with project context)
        WriteSubIssueFile("01-feature.json", "Add auth", "Auth module");

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "103", Url = "https://example.com/103" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — labels applied regardless of project context presence
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
    }

    [Fact]
    public async Task ExecuteAsync_CustomLabelsFromProposal_IncludedAlongsideAgentLabels()
    {
        // Arrange — sub-issue with custom labels (no targetRepository)
        WriteSubIssueFileWithLabels(
            "01-labeled.json", "Labeled feature", "Body",
            ["priority:high", "team:platform"]);

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "401", Url = "https://example.com/401" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — agent labels + custom labels all present
        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("priority:high");
        capturedLabels.Should().Contain("team:platform");
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateLabels_NotDuplicated()
    {
        // Arrange — sub-issue with custom label that matches an auto-applied label
        WriteSubIssueFileWithLabels(
            "01-dup.json", "Duplicate label test", "Body",
            ["agent:next", "custom-label"]);

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "402", Url = "https://example.com/402" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — agent:next not duplicated, custom-label added
        capturedLabels.Should().NotBeNull();
        capturedLabels!.Count(l => l == AgentLabels.Next).Should().Be(1);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("custom-label");
    }

    #endregion

    #region Helpers

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 10 },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    private PipelineStepContext BuildContextWithProjectContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 10 },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectContext = new DecompositionProjectContext
            {
                ProjectName = "TestProject",
                Repositories =
                [
                    new RepositoryTarget
                    {
                        TemplateName = "backend-api",
                        Description = "Backend REST API service",
                        DecompositionEnabled = true,
                        Available = true,
                        IssueProviderId = "provider-backend-001"
                    },
                    new RepositoryTarget
                    {
                        TemplateName = "frontend-web",
                        Description = "React web frontend",
                        DecompositionEnabled = true,
                        Available = true,
                        IssueProviderId = "provider-frontend-002"
                    }
                ]
            }
        };
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "100",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Decomposition,
        WorkspacePath = _workspacePath
    };

    private void WriteSubIssueFile(string filename, string title, string body)
    {
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = $$"""
        {
            "title": "{{title}}",
            "body": "{{body}}",
            "dependencies": [],
            "labels": []
        }
        """;
        File.WriteAllText(Path.Combine(dir, filename), json);
    }

    private void WriteSubIssueFileWithLabels(string filename, string title, string body, string[] labels)
    {
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var labelsJson = string.Join(", ", labels.Select(l => $"\"{l}\""));
        var json = $$"""
        {
            "title": "{{title}}",
            "body": "{{body}}",
            "dependencies": [],
            "labels": [{{labelsJson}}]
        }
        """;
        File.WriteAllText(Path.Combine(dir, filename), json);
    }

    #endregion
}
