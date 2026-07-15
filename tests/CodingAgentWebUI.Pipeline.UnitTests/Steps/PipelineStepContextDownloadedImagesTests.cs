using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Verifies the DownloadedImages property on PipelineStepContext
/// can be set and read by pipeline steps (Req 2 — image delivery via context).
/// </summary>
public class PipelineStepContextDownloadedImagesTests
{
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    [Fact]
    public void DownloadedImages_DefaultsToNull()
    {
        var context = BuildContext();

        context.DownloadedImages.Should().BeNull();
    }

    [Fact]
    public void DownloadedImages_CanBeSetAndRead()
    {
        var context = BuildContext();
        var images = new List<DownloadedImage>
        {
            new()
            {
                LocalPath = "/tmp/images/issue-42-image-001.png",
                LocalFilename = "issue-42-image-001.png",
                Reference = new ImageReference
                {
                    Url = "https://example.com/screenshot.png",
                    AltText = "Screenshot",
                    SourceType = ImageSourceType.Body,
                    SourceIndex = 0
                },
                FileSizeBytes = 102400,
                MimeType = "image/png"
            }
        };

        context.DownloadedImages = images;

        context.DownloadedImages.Should().NotBeNull();
        context.DownloadedImages.Should().HaveCount(1);
        context.DownloadedImages![0].LocalPath.Should().Be("/tmp/images/issue-42-image-001.png");
        context.DownloadedImages[0].Reference.Url.Should().Be("https://example.com/screenshot.png");
    }

    [Fact]
    public void DownloadedImages_CanBeSetToEmptyList()
    {
        var context = BuildContext();

        context.DownloadedImages = Array.Empty<DownloadedImage>();

        context.DownloadedImages.Should().NotBeNull();
        context.DownloadedImages.Should().BeEmpty();
    }

    private PipelineStepContext BuildContext()
    {
        return new PipelineStepContext
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Test Issue",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = PipelineRunType.Implementation,
                WorkspacePath = "/tmp/workspace"
            },
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = Mock.Of<IPipelineCallbacks>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }
}
