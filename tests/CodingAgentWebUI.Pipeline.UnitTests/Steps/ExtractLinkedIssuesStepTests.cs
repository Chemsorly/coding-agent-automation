using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="ExtractLinkedIssuesStep"/>.
/// Tests context population from linked issues and fallback to PR metadata.
/// Feature: 025-pr-review-pipeline, Requirements: Req 12
/// </summary>
public class ExtractLinkedIssuesStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<CancellationTokenSource> _tokenSources = new();

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        var cts = new CancellationTokenSource();
        _tokenSources.Add(cts);
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = run.WorkspacePath ?? "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = cts,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    [Fact]
    public async Task ExecuteAsync_WithLinkedIssues_PopulatesContextIssueFromFirstLinkedIssue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var linkedIssues = new List<LinkedIssueContext>
            {
                new() { Identifier = "42", Title = "First Issue", Description = "First issue description" },
                new() { Identifier = "43", Title = "Second Issue", Description = "Second issue description" }
            };

            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "10",
                IssueTitle = "Test PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "PR body text",
                LinkedIssueContexts = linkedIssues
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            await step.ExecuteAsync(context, CancellationToken.None);

            // Context.Issue should be populated from the first linked issue
            context.Issue.Should().NotBeNull();
            context.Issue!.Identifier.Should().Be("42");
            context.Issue.Title.Should().Be("First Issue");
            context.Issue.Description.Should().Be("First issue description");

            // Context.ParsedIssue should be populated
            context.ParsedIssue.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithLinkedIssues_PopulatesParsedIssue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-parsed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var linkedIssues = new List<LinkedIssueContext>
            {
                new()
                {
                    Identifier = "55",
                    Title = "Feature Request",
                    Description = "## Requirements\n- Must do X\n\n## Acceptance Criteria\n- [ ] X works"
                }
            };

            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "10",
                IssueTitle = "Test PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "PR body",
                LinkedIssueContexts = linkedIssues
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            await step.ExecuteAsync(context, CancellationToken.None);

            context.ParsedIssue.Should().NotBeNull();
            // The parser should have extracted something from the structured description
            context.ParsedIssue!.AcceptanceCriteria.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedIssues_FallsBackToPrMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "77",
                IssueTitle = "My PR Title",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "This PR adds pagination support",
                LinkedIssueContexts = Array.Empty<LinkedIssueContext>()
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            await step.ExecuteAsync(context, CancellationToken.None);

            // Context.Issue should be synthesized from PR metadata
            context.Issue.Should().NotBeNull();
            context.Issue!.Identifier.Should().Be("77");
            context.Issue.Title.Should().Be("My PR Title");
            context.Issue.Description.Should().Be("This PR adds pagination support");

            // Context.ParsedIssue should be populated from PR description
            context.ParsedIssue.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoLinkedIssues_NullLinkedIssueContexts_FallsBackToPrMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-null-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "88",
                IssueTitle = "Another PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "Some description",
                LinkedIssueContexts = null
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            await step.ExecuteAsync(context, CancellationToken.None);

            // Should fall back to PR metadata when LinkedIssueContexts is null
            context.Issue.Should().NotBeNull();
            context.Issue!.Identifier.Should().Be("88");
            context.Issue.Title.Should().Be("Another PR");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TransitionsToExtractingLinkedIssues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-transition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "5",
                IssueTitle = "PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "",
                LinkedIssueContexts = Array.Empty<LinkedIssueContext>()
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            await step.ExecuteAsync(context, CancellationToken.None);

            _callbacks.Verify(c => c.TransitionTo(PipelineStep.ExtractingLinkedIssues), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsContinue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test-extract-result-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var run = new PipelineRun
            {
                RunId = "test-run",
                IssueIdentifier = "1",
                IssueTitle = "PR",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                WorkspacePath = tempDir,
                RunType = PipelineRunType.Review,
                ReviewPrDescription = "desc",
                LinkedIssueContexts = Array.Empty<LinkedIssueContext>()
            };

            var context = BuildContext(run);
            var step = new ExtractLinkedIssuesStep(new IssueDescriptionParser());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    public void Dispose()
    {
        foreach (var cts in _tokenSources)
            cts.Dispose();
        (_logger as IDisposable)?.Dispose();
    }
}
