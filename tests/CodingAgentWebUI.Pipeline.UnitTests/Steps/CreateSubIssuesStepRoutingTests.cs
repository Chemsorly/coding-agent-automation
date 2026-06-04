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

    private void WriteSubIssueFileWithTargetRepository(string filename, string title, string body, string targetRepository)
    {
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = $$"""
        {
            "title": "{{title}}",
            "body": "{{body}}",
            "dependencies": [],
            "labels": ["enhancement"],
            "targetRepository": "{{targetRepository}}"
        }
        """;
        File.WriteAllText(Path.Combine(dir, filename), json);
    }

    #endregion

    #region End-to-End: JSON with targetRepository → Parse → Route → CreateIssueForProviderAsync

    [Fact]
    public async Task ExecuteAsync_JsonWithTargetRepository_RoutesToCorrectProvider()
    {
        // Arrange — write JSON file with targetRepository matching a template in the project context
        WriteSubIssueFileWithTargetRepository(
            "01-backend-feature.json", "Add API endpoint", "Implement GET /users", "backend-api");

        string? capturedProviderId = null;
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>(
                (providerId, _, _, _, _) => capturedProviderId = providerId)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "201", Url = "https://example.com/201" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — routed to CreateIssueForProviderAsync with the correct provider ID
        capturedProviderId.Should().Be("provider-backend-001");
        _issueOps.Verify(x => x.CreateIssueAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "Should NOT call default CreateIssueAsync when targetRepository resolves");
    }

    [Fact]
    public async Task ExecuteAsync_JsonWithTargetRepository_MatchesSecondTemplate_RoutesCorrectly()
    {
        // Arrange — target matches the second template in the project
        WriteSubIssueFileWithTargetRepository(
            "01-frontend-feature.json", "Add login page", "Create login form component", "frontend-web");

        string? capturedProviderId = null;
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>(
                (providerId, _, _, _, _) => capturedProviderId = providerId)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "202", Url = "https://example.com/202" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — routed to frontend provider
        capturedProviderId.Should().Be("provider-frontend-002");
    }

    [Fact]
    public async Task ExecuteAsync_JsonWithUnresolvableTargetRepository_FallsBackToDefault()
    {
        // Arrange — targetRepository does not match any template name
        WriteSubIssueFileWithTargetRepository(
            "01-unknown.json", "Some feature", "Implementation details", "nonexistent-repo");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "203", Url = "https://example.com/203" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — fell back to default CreateIssueAsync (not routed)
        _issueOps.Verify(x => x.CreateIssueAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(x => x.CreateIssueForProviderAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_JsonWithoutTargetRepository_UsesDefaultProvider()
    {
        // Arrange — no targetRepository field in JSON (backward compatible)
        WriteSubIssueFile("01-simple.json", "Simple feature", "Basic implementation");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "204", Url = "https://example.com/204" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — default path used (CreateIssueAsync, not CreateIssueForProviderAsync)
        _issueOps.Verify(x => x.CreateIssueAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(x => x.CreateIssueForProviderAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MixedTargetRepositories_RoutesEachCorrectly()
    {
        // Arrange — multiple sub-issues routed to different providers
        WriteSubIssueFileWithTargetRepository(
            "01-backend.json", "Backend work", "API changes", "backend-api");
        WriteSubIssueFileWithTargetRepository(
            "02-frontend.json", "Frontend work", "UI changes", "frontend-web");
        WriteSubIssueFile("03-default.json", "Default work", "Unrouted changes");

        var routedProviderIds = new List<string>();
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>(
                (providerId, _, _, _, _) => routedProviderIds.Add(providerId))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "300", Url = "https://example.com/300" });

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "301", Url = "https://example.com/301" });

        var run = CreateRun();
        var context = BuildContextWithProjectContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — two routed, one default
        routedProviderIds.Should().HaveCount(2);
        routedProviderIds.Should().Contain("provider-backend-001");
        routedProviderIds.Should().Contain("provider-frontend-002");
        _issueOps.Verify(x => x.CreateIssueAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region SubIssueFileParser — targetRepository field parsing

    [Fact]
    public async Task ParseSubIssueFiles_WithTargetRepository_PopulatesField()
    {
        // Arrange
        WriteSubIssueFileWithTargetRepository(
            "01-routed.json", "Routed issue", "Goes to backend", "backend-api");

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().Be("backend-api");
    }

    [Fact]
    public async Task ParseSubIssueFiles_WithoutTargetRepository_LeavesFieldNull()
    {
        // Arrange
        WriteSubIssueFile("01-simple.json", "Simple", "No routing");

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().BeNull();
    }

    [Fact]
    public async Task ParseSubIssueFiles_WithNullTargetRepository_LeavesFieldNull()
    {
        // Arrange — explicit JSON null
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = """
        {
            "title": "Null target",
            "body": "Body text",
            "dependencies": [],
            "labels": [],
            "targetRepository": null
        }
        """;
        File.WriteAllText(Path.Combine(dir, "01-null.json"), json);

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().BeNull();
    }

    [Fact]
    public async Task ParseSubIssueFiles_WithWrongTypeTargetRepository_StillParsesFile()
    {
        // Arrange — targetRepository as integer (wrong type, should be ignored but file accepted)
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = """
        {
            "title": "Wrong type target",
            "body": "Body text",
            "dependencies": [],
            "labels": [],
            "targetRepository": 42
        }
        """;
        File.WriteAllText(Path.Combine(dir, "01-wrongtype.json"), json);

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert — file accepted, targetRepository ignored
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().BeNull();
        proposals[0].Title.Should().Be("Wrong type target");
    }

    [Fact]
    public async Task ParseSubIssueFiles_WithEmptyStringTargetRepository_LeavesFieldNull()
    {
        // Arrange — empty string treated as absent
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = """
        {
            "title": "Empty target",
            "body": "Body text",
            "dependencies": [],
            "labels": [],
            "targetRepository": ""
        }
        """;
        File.WriteAllText(Path.Combine(dir, "01-empty.json"), json);

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().BeNull();
    }

    [Fact]
    public async Task ParseSubIssueFiles_WithPascalCaseTargetRepository_ParsesCorrectly()
    {
        // Arrange — PascalCase property name (alternative casing)
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = """
        {
            "title": "PascalCase target",
            "body": "Body text",
            "dependencies": [],
            "labels": [],
            "TargetRepository": "my-service"
        }
        """;
        File.WriteAllText(Path.Combine(dir, "01-pascal.json"), json);

        // Act
        var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(_workspacePath, _logger, CancellationToken.None);

        // Assert
        proposals.Should().HaveCount(1);
        proposals[0].TargetRepository.Should().Be("my-service");
    }

    #endregion
}
