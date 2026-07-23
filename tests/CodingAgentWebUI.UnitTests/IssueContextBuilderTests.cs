using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchInfrastructure.BuildIssueContextAsync"/>.
/// Verifies the issue context preparation logic (fetching, parsing,
/// comment capping, and basic staleness signal detection).
/// </summary>
public class IssueContextBuilderTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILabelService> _mockLabelService = new();

    private DispatchInfrastructure CreateInfrastructure()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        return new DispatchInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            resolution);
    }

    private void SetupIssueProvider(
        IReadOnlyList<IssueComment> comments,
        string issueDescription = "Test description")
    {
        var issueConfig = new ProviderConfig
        {
            Id = "issue-provider-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("issue-provider-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        // TODO: Mock setups use It.IsAny<string>() — should verify exact identifier ("42") is passed
        // to GetIssueAsync and ListCommentsAsync to catch bugs where BuildIssueContextAsync forwards wrong identifier.
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue Title",
                Description = issueDescription,
                Labels = Array.Empty<string>()
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    [Fact]
    public async Task BuildIssueContextAsync_WithValidConfig_ReturnsFetchedIssueContext()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Body = "First comment", Author = "user", CreatedAt = DateTime.UtcNow }
        };
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IssueDetail.Title.Should().Be("Test Issue Title");
        result.IssueDetail.Identifier.Should().Be("42");
        // TODO: Assertion is too weak — should verify ParsedIssue content reflects the input description
        // rather than just checking non-null, to catch bugs where wrong text is passed to the parser.
        result.ParsedIssue.Should().NotBeNull();
        result.IssueComments.Should().HaveCount(1);
        result.ExistingAnalysis.Should().BeNull();
        result.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
        result.RefreshCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildIssueContextAsync_WhenProviderConfigNotFound_ReturnsNull()
    {
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("missing-id", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "missing-id", CancellationToken.None);

        result.Should().BeNull();
    }

    // TODO: Add boundary test for exactly 50 comments — should pass through unmodified (code caps only > 50).
    [Fact]
    public async Task BuildIssueContextAsync_CapsCommentsAt50()
    {
        var comments = Enumerable.Range(1, 60).Select(i => new IssueComment
        {
            Id = $"c-{i}",
            Body = $"Comment {i}",
            Author = "user",
            CreatedAt = DateTime.UtcNow.AddMinutes(i)
        }).ToList();
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IssueComments.Should().HaveCount(50);
    }

    [Fact]
    public async Task BuildIssueContextAsync_WithExistingAnalysis_DetectsGateRejection()
    {
        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "c-analysis",
                Body = $"{CommentMarkers.AnalysisHeader}\nSome analysis content",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            },
            new()
            {
                Id = "c-rejection",
                Body = $"{CommentMarkers.GateRejection}\nRejection reason",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5) // Newer than analysis
            }
        };
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeTrue();
        result.StalenessSignal.Should().Be("gate_rejection");
    }

    [Fact]
    public async Task BuildIssueContextAsync_WithExistingAnalysis_DetectsGateWontDo()
    {
        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "c-analysis",
                Body = $"{CommentMarkers.AnalysisHeader}\nSome analysis content",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            },
            new()
            {
                Id = "c-wontdo",
                Body = $"{CommentMarkers.GateWontDo}\nWon't do reason",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5) // Newer than analysis
            }
        };
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeTrue();
        result.StalenessSignal.Should().Be("gate_wont_do");
    }

    [Fact]
    public async Task BuildIssueContextAsync_NoAnalysisComment_ReturnsNullExistingAnalysis()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Body = "Regular comment", Author = "user", CreatedAt = DateTime.UtcNow }
        };
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().BeNull();
        result.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
    }

    [Fact]
    public async Task BuildIssueContextAsync_GateRejectionOlderThanAnalysis_NoForceRefresh()
    {
        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "c-rejection",
                Body = $"{CommentMarkers.GateRejection}\nOld rejection",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-20) // Older than analysis
            },
            new()
            {
                Id = "c-analysis",
                Body = $"{CommentMarkers.AnalysisHeader}\nFresh analysis",
                Author = "bot",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5) // Newer than rejection
            }
        };
        SetupIssueProvider(comments);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
    }

    [Fact]
    public async Task BuildIssueContextAsync_ExtractsImagesFromBodyAndComments()
    {
        var issueDescription = "See the error below:\n\n![screenshot](https://github.com/user-attachments/assets/abc123.png)\n\nPlease fix this.";
        var comments = new List<IssueComment>
        {
            new()
            {
                Id = "c1",
                Body = "Here is another screenshot:\n\n<img src=\"https://github.com/user-attachments/assets/def456.png\" alt=\"error details\">",
                Author = "user",
                CreatedAt = DateTime.UtcNow
            }
        };
        SetupIssueProvider(comments, issueDescription);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IssueDetail.Images.Should().HaveCount(2);
        result.IssueDetail.Images[0].Url.Should().Be("https://github.com/user-attachments/assets/abc123.png");
        result.IssueDetail.Images[0].SourceType.Should().Be(ImageSourceType.Body);
        result.IssueDetail.Images[1].Url.Should().Be("https://github.com/user-attachments/assets/def456.png");
        result.IssueDetail.Images[1].SourceType.Should().Be(ImageSourceType.Comment);
    }
}
