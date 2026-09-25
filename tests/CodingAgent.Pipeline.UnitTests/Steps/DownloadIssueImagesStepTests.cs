using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

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

    // Injected into the step so image "downloads" never touch the real network (example.com URLs
    // otherwise incur real DNS + HTTP, ~20s per test and network-flaky). Returns 404 so the step
    // exercises its graceful-degradation path without downloading anything.
    private readonly StubImageHandler _imageHandler = new();

    private sealed class StubImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Captures the URI of the first outgoing HTTP request and returns 404 so the step
    /// exercises its graceful-degradation path without downloading anything.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? CapturedRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

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
                CreateRepoConfig(),
                _imageHandler);

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
                CreateRepoConfig(),
                _imageHandler);

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

    // TODO: Add a complementary negative test verifying that when Settings is populated with the old
    // PascalCase keys ("ApiUrl", "ProjectId") the relative URL is NOT resolved (CapturedRequestUri
    // does not match the GitLab API path). Without it, a tautological implementation that accepted
    // both casings would still pass the positive assertion. (TestQualityReviewer WARNING)
    [Fact]
    public async Task ExecuteAsync_GitLabRepoWithRelativeImageUrl_ResolvesApiUrlAndProjectIdFromSettings()
    {
        // Arrange: GitLab repo config with camelCase-keyed settings
        // TODO: Change ProjectId value to something distinct from Identifier/"42" (e.g. "9999") so the
        // assertion ".../projects/42/..." is sensitive to the correct source. Currently the issue
        // Identifier is also "42", meaning a bug that read Identifier instead of Settings[ProjectId]
        // would still pass. (TestQualityReviewer WARNING)
        var gitLabRepoConfig = new ProviderConfig
        {
            DisplayName = "GitLab Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://gitlab.example.com/api/v4",
                [ProviderSettingKeys.ProjectId] = "42"
            }
        };

        var run = CreateRun();
        Directory.CreateDirectory(run.WorkspacePath!);

        var capturingHandler = new CapturingHandler();
        try
        {
            var issue = new IssueDetail
            {
                Description = "GitLab issue with a relative upload",
                Identifier = "42",
                Labels = [],
                Title = "Test",
                Images =
                [
                    new ImageReference
                    {
                        Url = "/uploads/abc123secret/screenshot.png",
                        AltText = "screenshot",
                        SourceType = ImageSourceType.Body,
                        SourceIndex = 0
                    }
                ]
            };
            var context = BuildContext(run, issue: issue);
            var step = new DownloadIssueImagesStep(
                _ => Task.FromResult("gitlab-token"),
                gitLabRepoConfig,
                capturingHandler);

            // Act
            var result = await step.ExecuteAsync(context, CancellationToken.None);

            // Assert: the step resolved ApiUrl and ProjectId from the camelCase-keyed Settings
            // and constructed the correct GitLab API path before attempting the download.
            capturingHandler.CapturedRequestUri.Should().NotBeNull(
                because: "the step must read ApiUrl and ProjectId from Settings to resolve the relative GitLab URL");
            capturingHandler.CapturedRequestUri!.ToString()
                .Should().Be("https://gitlab.example.com/api/v4/projects/42/uploads/abc123secret/screenshot.png");

            // Graceful degradation: 404 from handler → empty downloaded list, not a pipeline failure
            result.Should().Be(StepResult.Continue);
            context.DownloadedImages.Should().NotBeNull()
                .And.BeEmpty(because: "the 404 response means no images were successfully downloaded");
        }
        finally
        {
            capturingHandler.Dispose();
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
        _imageHandler.Dispose();
    }
}
