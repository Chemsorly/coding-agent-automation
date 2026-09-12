using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests;

/// <summary>
/// Unit tests for <see cref="DispatchInfrastructure.BuildIssueContextAsync"/>.
/// Verifies the issue context preparation logic (fetching, parsing,
/// comment capping, and basic staleness signal detection).
/// </summary>
// TODO: add [Collection("SerilogLoggerTests")] once a [CollectionDefinition("SerilogLoggerTests")]
// class exists in this project (see IssueContextBuilderImageExtractionFailureTests below). Without
// the definition the collection attribute is a no-op and this class can run in parallel with
// IssueContextBuilderImageExtractionFailureTests, causing Log.Logger races in CI.
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
    public async Task BuildIssueContextAsync_PreservesIssueUrl_ThroughImageExtraction()
    {
        // Regression test: the image-extraction path reconstructs a new IssueDetail object.
        // Verify that IssueDetail.Url from the provider is NOT dropped during that reconstruction.
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue",
                Description = "No images here.",
                Labels = Array.Empty<string>(),
                Url = "https://github.com/owner/repo/issues/42"
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        var issueConfig = new ProviderConfig
        {
            Id = "issue-url-test",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("issue-url-test", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var infra = CreateInfrastructure();
        var result = await infra.BuildIssueContextAsync("42", "issue-url-test", CancellationToken.None);

        result.Should().NotBeNull();
        result!.IssueDetail.Url.Should().Be("https://github.com/owner/repo/issues/42",
            "IssueDetail.Url must be preserved through the image-extraction reconstruction in BuildIssueContextAsync");
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

// ── Image extraction failure tests ───────────────────────────────────────────

/// <summary>
/// Tests for <see cref="DispatchInfrastructure.BuildIssueContextAsync"/> image-extraction
/// failure path. Uses <c>[Collection("SerilogLoggerTests")]</c> to serialize access to the
/// global <see cref="Log.Logger"/> alongside other tests that swap it.
/// </summary>
// TODO: create a [CollectionDefinition("SerilogLoggerTests", DisableParallelization = true)] class
// in this project (mirroring ActivityListenerTestsCollection.cs). Without it, xUnit treats each
// [Collection("SerilogLoggerTests")] class as an independent single-class collection and does NOT
// serialize them against each other, so concurrent Log.Logger swaps in JobTemplateStoreLoggingTests,
// ChatDispatcherObservabilityTests, and JobSpecBuilderLoggingTests can still race with this test.
[Collection("SerilogLoggerTests")]
public class IssueContextBuilderImageExtractionFailureTests
{
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILabelService> _mockLabelService = new();

    private DispatchInfrastructure CreateThrowingInfrastructure()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        return new ThrowingExtractInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            resolution);
    }

    private void SetupIssueProvider(string issueDescription = "Issue body with an image")
    {
        var issueConfig = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        };
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "org/repo#42",
                Title = "Test Issue",
                Description = issueDescription,
                Labels = Array.Empty<string>()
            });
        mockIssueProvider
            .Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
    }

    [Fact]
    public async Task BuildIssueContextAsync_WhenImageExtractionThrows_LogsWarningAndReturnsEmptyImages()
    {
        // Arrange
        SetupIssueProvider();
        var infra = CreateThrowingInfrastructure();

        // Replace Serilog.Log.Logger with a capturing sink to assert the Warning is emitted.
        // Restored in the finally block to avoid polluting other tests.
        var capturedEvents = new System.Collections.Concurrent.ConcurrentBag<LogEvent>();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new CapturingSink(capturedEvents))
            .CreateLogger();

        try
        {
            // Act — must not throw despite extraction failure
            var result = await infra.BuildIssueContextAsync(
                new IssueIdentifier("org/repo#42"), new ProviderConfigId("ip-1"), CancellationToken.None);

            // Assert: dispatch continued
            result.Should().NotBeNull(
                "extraction failure must not abort dispatch");

            // Assert: Images is empty (extraction failed, fallback used)
            result!.IssueDetail.Images.Should().BeEmpty(
                "dispatch must continue with empty Images when extraction throws");

            // Assert: a Warning-level log event was emitted containing the issue identifier
            // TODO: replace ContainSingle with Contain (at-least-one) or narrow the filter with a
            // specific message-template substring (e.g. "image extraction failed") so that a second
            // unrelated Warning emitted by BuildIssueContextAsync doesn't cause a misleading failure.
            // TODO: also assert e.Properties.ContainsKey("IssueIdentifier") to verify the structured
            // log property is present, rather than relying solely on RenderMessage() string matching
            // (RenderMessage is fragile if IssueIdentifier.ToString() format ever changes).
            capturedEvents.Should().ContainSingle(
                e => e.Level == LogEventLevel.Warning
                     && e.RenderMessage().Contains("org/repo#42"),
                "a Warning including the issue identifier must be logged on extraction failure");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────

    /// <summary>
    /// Test subclass that overrides <see cref="DispatchInfrastructure.ExtractImages"/> to throw,
    /// enabling deterministic testing of the extraction-failure path without mocking the sealed
    /// <see cref="IssueImageExtractor"/>.
    /// </summary>
    private sealed class ThrowingExtractInfrastructure : DispatchInfrastructure
    {
        public ThrowingExtractInfrastructure(
            ITokenVendingService tokenVending,
            IProviderFactory providerFactory,
            ILabelService labelService,
            DispatchResolutionService resolution)
            : base(tokenVending, providerFactory, labelService, resolution) { }

        protected override IReadOnlyList<ImageReference> ExtractImages(
            string description,
            IReadOnlyList<IssueComment> comments,
            IssueIdentifier issueIdentifier)
            => throw new InvalidOperationException("simulated extraction failure");
    }

    /// <summary>
    /// Thread-safe Serilog sink that captures all emitted events for test assertions.
    /// </summary>
    private sealed class CapturingSink(System.Collections.Concurrent.ConcurrentBag<LogEvent> events)
        : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }
}
