using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="DownloadIssueImagesStep"/>.
/// Feature: 037-issue-image-extraction, Requirements: Req 2, Req 8
/// </summary>
public class DownloadIssueImagesStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<CancellationTokenSource> _tokenSources = new();

    public DownloadIssueImagesStepTests()
    {
        _agentProvider.Setup(p => p.SupportsVisionInput).Returns(true);
    }

    private PipelineStepContext BuildContext(
        PipelineRun run,
        PipelineConfiguration? config = null,
        IssueDetail? issue = null)
    {
        var cts = new CancellationTokenSource();
        _tokenSources.Add(cts);
        var ctx = new PipelineStepContext
        {
            Run = run,
            Config = config ?? new PipelineConfiguration
            {
                WorkspaceBaseDirectory = run.WorkspacePath ?? "/tmp",
                EnableIssueImageExtraction = true
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _agentProvider.Object,
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
        ctx.Issue = issue;
        return ctx;
    }

    private static PipelineRun CreateRun(string? workspacePath = null) => new()
    {
        RunId = $"test-{Guid.NewGuid():N}",
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        WorkspacePath = workspacePath ?? Path.Combine(Path.GetTempPath(), $"test-download-{Guid.NewGuid():N}"),
        RunType = PipelineRunType.Implementation
    };

    private static ProviderConfig CreateRepoConfig() => new()
    {
        DisplayName = "Test Repo",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        Settings = new Dictionary<string, string>()
    };

    [Fact]
    public async Task ExecuteAsync_ConfigDisabled_ReturnsContinueWithoutDownload()
    {
        var run = CreateRun();
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            var config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = run.WorkspacePath!,
                EnableIssueImageExtraction = false
            };
            var issue = new IssueDetail
            {
                Description = "Has ![img](https://example.com/img.png)",
                Identifier = "42",
                Labels = [],
                Title = "Test",
                Images = [new ImageReference { Url = "https://example.com/img.png", AltText = "img", SourceType = ImageSourceType.Body, SourceIndex = 0 }]
            };
            var context = BuildContext(run, config, issue);
            var tokenCalled = false;
            var step = new DownloadIssueImagesStep(
                _ => { tokenCalled = true; return Task.FromResult("token"); },
                CreateRepoConfig());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            tokenCalled.Should().BeFalse();
            context.DownloadedImages.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ModelDoesNotSupportVision_ReturnsContinueWithoutDownload()
    {
        _agentProvider.Setup(p => p.SupportsVisionInput).Returns(false);

        var run = CreateRun();
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            var issue = new IssueDetail
            {
                Description = "Has ![img](https://example.com/img.png)",
                Identifier = "42",
                Labels = [],
                Title = "Test",
                Images = [new ImageReference { Url = "https://example.com/img.png", AltText = "img", SourceType = ImageSourceType.Body, SourceIndex = 0 }]
            };
            var context = BuildContext(run, issue: issue);
            var tokenCalled = false;
            var step = new DownloadIssueImagesStep(
                _ => { tokenCalled = true; return Task.FromResult("token"); },
                CreateRepoConfig());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            tokenCalled.Should().BeFalse();
            context.DownloadedImages.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoImages_ReturnsContinueWithoutDownload()
    {
        var run = CreateRun();
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            var issue = new IssueDetail
            {
                Description = "No images here",
                Identifier = "42",
                Labels = [],
                Title = "Test",
                Images = []
            };
            var context = BuildContext(run, issue: issue);
            var tokenCalled = false;
            var step = new DownloadIssueImagesStep(
                _ => { tokenCalled = true; return Task.FromResult("token"); },
                CreateRepoConfig());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            tokenCalled.Should().BeFalse();
            context.DownloadedImages.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TokenRefreshFails_ReturnsContinueGracefully()
    {
        var run = CreateRun();
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            var issue = new IssueDetail
            {
                Description = "Has image",
                Identifier = "42",
                Labels = [],
                Title = "Test",
                Images = [new ImageReference { Url = "https://example.com/img.png", AltText = "img", SourceType = ImageSourceType.Body, SourceIndex = 0 }]
            };
            var context = BuildContext(run, issue: issue);
            var step = new DownloadIssueImagesStep(
                _ => throw new InvalidOperationException("Token refresh failed"),
                CreateRepoConfig());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            context.DownloadedImages.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRun_ExtractsFromReviewPrDescription()
    {
        var run = new PipelineRun
        {
            RunId = $"test-{Guid.NewGuid():N}",
            IssueIdentifier = "55",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"test-download-{Guid.NewGuid():N}"),
            RunType = PipelineRunType.Review,
            ReviewPrDescription = "PR body with ![screenshot](https://example.com/screenshot.png)"
        };
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            // Issue has no images, but PR description does
            var issue = new IssueDetail
            {
                Description = "No images",
                Identifier = "55",
                Labels = [],
                Title = "Test",
                Images = []
            };
            var context = BuildContext(run, issue: issue);
            // Token will be requested since we have PR images
            var tokenRequested = false;
            var step = new DownloadIssueImagesStep(
                _ => { tokenRequested = true; return Task.FromResult("token"); },
                CreateRepoConfig());

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            // Token should be requested because images were found from PR description
            tokenRequested.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRun_MergesIssueAndPrImages()
    {
        var run = new PipelineRun
        {
            RunId = $"test-{Guid.NewGuid():N}",
            IssueIdentifier = "60",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"test-download-{Guid.NewGuid():N}"),
            RunType = PipelineRunType.Review,
            ReviewPrDescription = "PR body with ![pr-img](https://example.com/pr-image.png)"
        };
        Directory.CreateDirectory(run.WorkspacePath!);
        try
        {
            // Issue has one image AND PR description has another
            var issue = new IssueDetail
            {
                Description = "Issue text",
                Identifier = "60",
                Labels = [],
                Title = "Test",
                Images = [new ImageReference { Url = "https://example.com/issue-image.png", AltText = "issue-img", SourceType = ImageSourceType.Body, SourceIndex = 0 }]
            };
            var context = BuildContext(run, issue: issue);
            var tokenRequested = false;
            var step = new DownloadIssueImagesStep(
                _ => { tokenRequested = true; return Task.FromResult("token"); },
                CreateRepoConfig());

            // Step will attempt download — token should be requested for merged images
            var result = await step.ExecuteAsync(context, CancellationToken.None);

            result.Should().Be(StepResult.Continue);
            tokenRequested.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(run.WorkspacePath!))
                Directory.Delete(run.WorkspacePath!, recursive: true);
        }
    }

    [Fact]
    public void Constructor_NullTokenProvider_ThrowsArgumentNullException()
    {
        var act = () => new DownloadIssueImagesStep(null!, CreateRepoConfig());
        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("tokenProvider");
    }

    [Fact]
    public void Constructor_NullRepoConfig_ThrowsArgumentNullException()
    {
        var act = () => new DownloadIssueImagesStep(_ => Task.FromResult("token"), null!);
        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("repoConfig");
    }

    [Fact]
    public void StepName_ReturnsExpectedValue()
    {
        var step = new DownloadIssueImagesStep(_ => Task.FromResult("t"), CreateRepoConfig());
        step.StepName.Should().Be("DownloadIssueImages");
    }

    public void Dispose()
    {
        foreach (var cts in _tokenSources)
            cts.Dispose();
        (_logger as IDisposable)?.Dispose();
    }
}
