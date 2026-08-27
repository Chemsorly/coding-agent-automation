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
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockRepo.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepo.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockRepo.Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/pull/99");
        _mockRepo.Setup(r => r.BaseBranch).Returns("main");
        _mockRepo.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #42");
    }

    // ── No commits ahead → early return ──

    [Fact]
    public async Task CreatePullRequest_NoCommitsAhead_ReturnsNull()
    {
        _mockRepo.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
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
        _mockRepo.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
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
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(blacklisted.AsReadOnly());

        var run = CreateRun();
        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
        run.BlacklistedFilesDetected.Should().Contain(".agent/config.json");
    }

    // ── Blacklisted files → PR body no longer includes blacklist section ──

    [Fact]
    public async Task CreatePullRequest_BlacklistedFiles_NotIncludedInPrBody()
    {
        var blacklisted = new List<string> { ".github/workflows/ci.yml" };
        _mockRepo.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(blacklisted.AsReadOnly());

        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        await _sut.CreatePullRequestAsync(
            run, CreateReport(), false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedInfo!.Body.Should().NotContain("## ⚠️ Blacklisted Files Excluded");
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
        _mockRepo.Verify(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PR body characterization tests — guard behavioral equivalence across both paths ──

    [Fact]
    public async Task CreatePullRequest_PrBodyContainsExpectedFields()
    {
        PullRequestInfo? capturedInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        var run = CreateRun();
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK", TestsPassed = 5, TestsFailed = 0, TestsSkipped = 1 }
        };

        await _sut.CreatePullRequestAsync(
            run, report, false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);
        // TODO: [WARNING] Both `issue` and `issueComments` are null here, so issueTitle falls back to run.IssueTitle
        // ("Test Issue"). The `issue?.Title ?? run.IssueTitle` branch in BuildPrBodyAsync where a real IssueDetail
        // with a *different* title takes precedence over the run title is not exercised. Add a complementary test
        // that passes a non-null IssueDetail with a distinct title to confirm issue title wins.

        capturedInfo!.Body.Should().Contain("#42");
        capturedInfo.Body.Should().Contain("Closes #42");
        // TODO: [WARNING] ".Contain("5")" is under-constrained — the digit "5" appears anywhere in a typical PR body
        // (PR numbers, coverage values, etc.) so this does not reliably guard TestsPassed rendering.
        // Replace with the exact format string emitted by PipelineFormatting.GeneratePrBody for the passed-test count
        // (e.g. "5 passed" or similar) once the template output is confirmed.
        capturedInfo.Body.Should().Contain("5");   // tests passed
        run.PullRequestBody.Should().Be(capturedInfo.Body);
    }

    [Fact]
    public async Task FinalizePullRequest_PrBodyContainsExpectedFields()
    {
        string? capturedBody = null;
        _mockRepo.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        run.PullRequestNumber = "55";
        run.PullRequestUrl = "https://github.com/org/repo/pull/55";
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK", TestsPassed = 5, TestsFailed = 0, TestsSkipped = 1 }
        };

        await _sut.FinalizePullRequestAsync(
            run, report, false, _mockRepo.Object,
            null, null, CreateConfig(), CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("#42");
        capturedBody.Should().Contain("Closes #42");
        // TODO: [WARNING] ".Contain("5")" is under-constrained — the digit "5" appears anywhere in a typical PR body
        // (PR numbers, coverage values, etc.) so this does not reliably guard TestsPassed rendering.
        // Replace with the exact format string emitted by PipelineFormatting.GeneratePrBody for the passed-test count
        // (e.g. "5 passed" or similar) once the template output is confirmed.
        capturedBody.Should().Contain("5");   // tests passed
        run.PullRequestBody.Should().Be(capturedBody);
    }

    [Fact]
    public async Task CreateAndFinalize_SameInputs_ProduceIdenticalBodies()
    {
        // Capture body from CreatePullRequestAsync (new-PR path)
        PullRequestInfo? capturedPrInfo = null;
        _mockRepo.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .Callback<PullRequestInfo, CancellationToken>((info, _) => capturedPrInfo = info)
            .ReturnsAsync("https://github.com/org/repo/pull/99");

        // Capture body from FinalizePullRequestAsync
        string? capturedFinalizeBody = null;
        _mockRepo.Setup(r => r.UpdatePullRequestAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, bool, CancellationToken>((_, body, _, _) => capturedFinalizeBody = body)
            .Returns(Task.CompletedTask);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK", TestsPassed = 3, TestsFailed = 1, TestsSkipped = 0 }
        };
        var issue = new IssueDetail { Title = "My Issue", Identifier = "42", Description = "", Labels = [] };

        // Run CreatePullRequestAsync
        var createRun = CreateRun();
        await _sut.CreatePullRequestAsync(
            createRun, report, false, _mockRepo.Object,
            issue, null, CreateConfig(), CancellationToken.None);

        // Run FinalizePullRequestAsync with identical inputs
        var finalizeRun = CreateRun();
        finalizeRun.PullRequestNumber = "88";
        finalizeRun.PullRequestUrl = "https://github.com/org/repo/pull/88";
        await _sut.FinalizePullRequestAsync(
            finalizeRun, report, false, _mockRepo.Object,
            issue, null, CreateConfig(), CancellationToken.None);

        capturedPrInfo!.Body.Should().Be(capturedFinalizeBody);
        // TODO: [WARNING] This test uses two separate PipelineRun instances with different PullRequestNumber/PullRequestUrl
        // values. If BuildPrBodyAsync or a downstream helper ever incorporates the existing PR number/URL, or if
        // BlacklistedFilesDetected is populated differently between the create and finalize execution paths, the two bodies
        // could legitimately differ and trigger a false failure. Consider extracting BuildPrBodyAsync into a standalone
        // unit test that calls the helper directly with identical inputs, rather than going through the full orchestrator
        // paths which carry per-run side effects.
        // Additionally, run.PullRequestBody is not asserted on either run here — the individual path tests do check it,
        // but this equivalence test does not confirm consistent run state updates in both paths.
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
