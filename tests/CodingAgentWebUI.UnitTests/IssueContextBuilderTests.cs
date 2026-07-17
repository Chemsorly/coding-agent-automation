using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="IssueContextBuilder"/>.
/// Verifies the extracted issue context preparation logic (fetching, parsing,
/// comment capping, and basic staleness signal detection).
/// </summary>
public class IssueContextBuilderTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IProviderConfigStore> _mockConfigStore = new();

    private IssueContextBuilder CreateBuilder() =>
        new(_mockProviderFactory.Object, _mockConfigStore.Object);

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
        // to GetIssueAsync and ListCommentsAsync to catch bugs where BuildAsync forwards wrong identifier.
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue Title",
                Description = issueDescription,
                Labels = Array.Empty<string>()
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(comments);
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    [Fact]
    public async Task BuildAsync_WithValidConfig_ReturnsFetchedIssueContext()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Body = "First comment", Author = "user", CreatedAt = DateTime.UtcNow }
        };
        SetupIssueProvider(comments);

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

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
    public async Task BuildAsync_WhenProviderConfigNotFound_ReturnsNull()
    {
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("missing-id", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "missing-id", CancellationToken.None);

        result.Should().BeNull();
    }

    // TODO: Add boundary test for exactly 50 comments — should pass through unmodified (code caps only > 50).
    [Fact]
    public async Task BuildAsync_CapsCommentsAt50()
    {
        var comments = Enumerable.Range(1, 60).Select(i => new IssueComment
        {
            Id = $"c-{i}",
            Body = $"Comment {i}",
            Author = "user",
            CreatedAt = DateTime.UtcNow.AddMinutes(i)
        }).ToList();
        SetupIssueProvider(comments);

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IssueComments.Should().HaveCount(50);
    }

    [Fact]
    public async Task BuildAsync_WithExistingAnalysis_DetectsGateRejection()
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

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeTrue();
        result.StalenessSignal.Should().Be("gate_rejection");
    }

    [Fact]
    public async Task BuildAsync_WithExistingAnalysis_DetectsGateWontDo()
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

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeTrue();
        result.StalenessSignal.Should().Be("gate_wont_do");
    }

    [Fact]
    public async Task BuildAsync_NoAnalysisComment_ReturnsNullExistingAnalysis()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "c1", Body = "Regular comment", Author = "user", CreatedAt = DateTime.UtcNow }
        };
        SetupIssueProvider(comments);

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().BeNull();
        result.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_GateRejectionOlderThanAnalysis_NoForceRefresh()
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

        var builder = CreateBuilder();
        var result = await builder.BuildAsync("42", "issue-provider-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExistingAnalysis.Should().Contain(CommentMarkers.AnalysisHeader);
        result.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
    }
}
