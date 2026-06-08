using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PullRequestOrchestratorTests
{
    private readonly Mock<IRepositoryProvider> _mockRepo = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly PullRequestOrchestrator _sut;

    public PullRequestOrchestratorTests()
    {
        _sut = new PullRequestOrchestrator(_mockLogger.Object);

        // Default happy-path setup
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockRepo.Setup(r => r.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockRepo.Setup(r => r.GetFileChangesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/pull/99");
        _mockRepo.Setup(r => r.BaseBranch).Returns("main");
        _mockRepo.Setup(r => r.FormatCloseReference(It.IsAny<string>())).Returns("Closes #42");
    }

    // ── No commits ahead → early return ──

    [Fact]
    public async Task CreatePullRequest_NoCommitsAhead_ReturnsNull()
    {
        _mockRepo.Setup(r => r.HasCommitsAheadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.CreatePullRequestAsync(
            CreateRun(), CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        result.Should().BeNull();
        _mockRepo.Verify(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Happy path — new PR ──

    [Fact]
    public async Task CreatePullRequest_HappyPath_CreatesPrAndSetsRunState()
    {
        var run = CreateRun();

        var result = await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        result.Should().Be("https://github.com/org/repo/pull/99");
        run.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/99");
        run.PullRequestNumber.Should().Be("99");
        run.CompletedAt.Should().NotBeNull();
        run.IsDraftPr.Should().BeFalse();
    }

    // ── Happy path — draft PR ──

    [Fact]
    public async Task CreatePullRequest_Draft_SetsDraftFlag()
    {
        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        await _sut.CreatePullRequestAsync(
            run, CreateReport(), true, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedInfo!.IsDraft.Should().BeTrue();
        run.IsDraftPr.Should().BeTrue();
    }

    // ── Push failure propagates ──

    [Fact]
    public async Task CreatePullRequest_PushFails_ExceptionPropagates()
    {
        _mockRepo.Setup(r => r.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("permission denied"));

        var act = () => _sut.CreatePullRequestAsync(
            CreateRun(), CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("permission denied");
    }

    // ── Blacklisted files detected → recorded on run ──

    [Fact]
    public async Task CreatePullRequest_BlacklistedFiles_RecordsOnRun()
    {
        var blacklisted = new List<string> { ".github/workflows/ci.yml", ".agent/config.json" };
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted.AsReadOnly());

        var run = CreateRun();
        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
        run.BlacklistedFilesDetected.Should().Contain(".agent/config.json");
    }

    // ── Blacklisted files → PR body includes blacklist section ──

    [Fact]
    public async Task CreatePullRequest_BlacklistedFiles_IncludedInPrBody()
    {
        var blacklisted = new List<string> { ".github/workflows/ci.yml" };
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(blacklisted.AsReadOnly());

        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedInfo!.Body.Should().Contain(".github/workflows/ci.yml");
    }

    // ── Code review summary included ──

    [Fact]
    public async Task CreatePullRequest_WithCodeReview_IncludesSummaryInBody()
    {
        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        run.CodeReviewAgentsRun = new List<string> { "Correctness", "Security" };
        run.SetCodeReviewCounts(2, 1, 0);

        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedInfo!.Body.Should().Contain("Code Review");
    }

    // ── No code review data → no summary section ──

    [Fact]
    public async Task CreatePullRequest_NoCodeReview_OmitsSummarySection()
    {
        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        // CodeReviewAgentsRun is empty by default

        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedInfo!.Body.Should().NotContain("Code Review");
    }

    // ── Rework path — updates existing PR ──

    [Fact]
    public async Task CreatePullRequest_Rework_CallsUpdateInsteadOfCreate()
    {
        _mockRepo.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        run.PullRequestUrl = "https://github.com/org/repo/pull/55";
        run.PullRequestNumber = "55";

        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None, isRework: true);

        _mockRepo.Verify(r => r.UpdatePullRequestAsync(55, It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepo.Verify(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Rework path — marks ready when not draft ──

    [Fact]
    public async Task CreatePullRequest_ReworkNotDraft_MarksReady()
    {
        _mockRepo.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        run.PullRequestUrl = "https://github.com/org/repo/pull/55";
        run.PullRequestNumber = "55";

        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None, isRework: true);

        _mockRepo.Verify(r => r.UpdatePullRequestAsync(55, It.IsAny<string>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FinalizePullRequestAsync — no PR number → returns null ──

    [Fact]
    public async Task FinalizePullRequest_NoPrNumber_ReturnsNull()
    {
        var run = CreateRun();
        // PullRequestNumber is null by default

        var result = await _sut.FinalizePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        result.Should().BeNull();
        _mockRepo.Verify(r => r.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CreateDraftPrIfNotExistsAsync — PR already exists → skip ──

    [Fact]
    public async Task CreateDraftPrIfNotExists_LinkedPrSet_SkipsCreation()
    {
        var run = CreateRun();
        run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 10, BranchName = "feature/x", Url = "https://github.com/org/repo/pull/10", IsDraft = false
        };

        var result = await _sut.CreateDraftPrIfNotExistsAsync(run, _mockRepo.Object, CancellationToken.None);

        result.Should().Be("https://github.com/org/repo/pull/10");
        _mockRepo.Verify(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateDraftPrIfNotExists_PrUrlSet_SkipsCreation()
    {
        var run = CreateRun();
        run.PullRequestUrl = "https://github.com/org/repo/pull/20";

        var result = await _sut.CreateDraftPrIfNotExistsAsync(run, _mockRepo.Object, CancellationToken.None);

        result.Should().Be("https://github.com/org/repo/pull/20");
        _mockRepo.Verify(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ──

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-pr",
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = "/tmp/workspace",
        BranchName = "feature/auto-42-test"
    };

    private static QualityGateReport CreateReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    private static PipelineConfiguration CreateConfig() => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = 0
    };
}
