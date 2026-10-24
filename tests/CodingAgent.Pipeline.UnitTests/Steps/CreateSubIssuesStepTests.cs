using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="CreateSubIssuesStep"/>.
/// Tests timeout enforcement, retry behavior, cap enforcement, and partial failure handling.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 4.6, 4.12, 10.3, 10.4
/// </summary>
public class CreateSubIssuesStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public CreateSubIssuesStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"create-sub-issues-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 5 },
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

    [Fact]
    public async Task ExecuteAsync_NoSubIssueFiles_ReturnsEmptyResults()
    {
        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_SingleValidProposal_CreatesIssueSuccessfully()
    {
        WriteSubIssueFile("01-add-auth.json", "Add authentication", "Implement auth module");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "101", Url = "https://github.com/test/101" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().HaveCount(1);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[0].Identifier.Should().Be("101");
        run.DecompositionSubIssuesCreated.Should().Be(1);
        run.DecompositionSubIssuesAttempted.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_TransientError_RetriesUpTo3Times()
    {
        WriteSubIssueFile("01-feature.json", "Add feature", "Feature body");

        var callCount = 0;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount < 3)
                    throw new HttpRequestException("Server error", null, System.Net.HttpStatusCode.InternalServerError);
                return Task.FromResult(new CreatedIssueResult { Identifier = "201", Url = "https://github.com/test/201" });
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep(retryDelayOverride: TimeSpan.Zero); // no real backoff wait

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        callCount.Should().Be(3); // 2 failures + 1 success
        run.SubIssueResults[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_TransientError_ExhaustsRetries_MarksAsFailed()
    {
        WriteSubIssueFile("01-feature.json", "Add feature", "Feature body");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Server error", null, System.Net.HttpStatusCode.InternalServerError));

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep(retryDelayOverride: TimeSpan.Zero); // no real backoff wait

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults[0].Success.Should().BeFalse();
        run.SubIssueResults[0].FailureReason.Should().Contain("Transient error after 3 attempts");
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientError_SkipsWithoutRetry()
    {
        WriteSubIssueFile("01-feature.json", "Add feature", "Feature body");

        var callCount = 0;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                throw new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        callCount.Should().Be(1); // No retries for non-transient
        run.SubIssueResults[0].Success.Should().BeFalse();
        run.SubIssueResults[0].FailureReason.Should().Contain("Non-transient error");
    }

    [Fact]
    public async Task ExecuteAsync_CapEnforced_ProcessesOnlyFirstN()
    {
        // Write 7 sub-issue files but cap is 5
        for (var i = 1; i <= 7; i++)
            WriteSubIssueFile($"{i:D2}-issue-{i}.json", $"Issue {i}", $"Body {i}");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "300", Url = "https://github.com/test/300" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        // All 7 appear in results: 5 created + 2 skipped by cap
        run.SubIssueResults.Should().HaveCount(7);
        run.DecompositionSubIssuesAttempted.Should().Be(5);

        // First 5 should be created successfully
        run.SubIssueResults.Take(5).Should().AllSatisfy(r => r.Success.Should().BeTrue());

        // Last 2 should be SkippedByCap
        var skipped = run.SubIssueResults.Skip(5).ToList();
        skipped.Should().HaveCount(2);
        skipped.Should().AllSatisfy(r =>
        {
            r.SkippedByCap.Should().BeTrue();
            r.Success.Should().BeFalse();
            r.FailureReason.Should().Contain("cap");
        });
        // TODO: These assertions do not verify the Title field of each SkippedByCap result nor that
        // the Logger.Information call receives the correct title after the Select projection in
        // CreateSubIssuesStep (site 19: skippedProposals.Select(p => p.Title)). A lambda that
        // accidentally selects a different property (e.g. p.Body) would still pass these checks.
        // Consider asserting r.Title == $"Issue {i}" for each skipped entry to lock in the projection.
    }

    [Fact]
    public async Task ExecuteAsync_CapEnforced_SkippedProposalsHaveCapReasonWithCapNumber()
    {
        // Write 3 sub-issue files but cap is 1
        for (var i = 1; i <= 3; i++)
            WriteSubIssueFile($"{i:D2}-issue-{i}.json", $"Issue {i}", $"Body {i}");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "301", Url = "https://github.com/test/301" });

        var run = CreateRun();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 1 },
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
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // 1 created + 2 skipped
        run.SubIssueResults.Should().HaveCount(3);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[0].SkippedByCap.Should().BeFalse();

        for (var i = 1; i < 3; i++)
        {
            run.SubIssueResults[i].SkippedByCap.Should().BeTrue();
            run.SubIssueResults[i].Success.Should().BeFalse();
            // TODO: Contain("1") is too weak — it matches any string containing the digit '1',
            // including words like "configured" are unrelated occurrences. The intent is to verify
            // the cap number is named in the reason. Change to a more specific assertion such as:
            // .Should().Contain("cap of 1") or .Should().Contain("1 sub-issue")
            run.SubIssueResults[i].FailureReason.Should().Contain("1");
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoCapping_NoSkippedByCap()
    {
        // Write 3 sub-issue files, cap is 5 — no capping should happen
        for (var i = 1; i <= 3; i++)
            WriteSubIssueFile($"{i:D2}-issue-{i}.json", $"Issue {i}", $"Body {i}");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "302", Url = "https://github.com/test/302" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        run.SubIssueResults.Should().HaveCount(3);
        run.SubIssueResults.Should().AllSatisfy(r => r.SkippedByCap.Should().BeFalse());
    }

    [Fact]
    public async Task ExecuteAsync_AppliesAgentNextAndAgentGeneratedLabels()
    {
        WriteSubIssueFile("01-feature.json", "Add feature", "Feature body");

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "400", Url = "https://github.com/test/400" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
    }

    // ── Issue #3159: label filtering ────────────────────────────────────

    private void WriteSubIssueFileWithLabels(string filename, string title, string body, IEnumerable<string> labels)
    {
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var labelArray = string.Join(", ", labels.Select(l => $"\"{l}\""));
        var json = $$"""
        {
            "title": "{{title}}",
            "body": "{{body}}",
            "dependencies": [],
            "labels": [{{labelArray}}]
        }
        """;
        File.WriteAllText(Path.Combine(dir, filename), json);
    }

    [Fact]
    public async Task ExecuteAsync_ProposalWithEpicApproved_DropsThatLabel()
    {
        // A sub-issue proposal that lists agent:epic-approved must create the issue without it.
        WriteSubIssueFileWithLabels(
            "01-epic.json", "Sub issue with gated label", "Body",
            [AgentLabels.EpicApproved, "backend"]);

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "600", Url = "https://github.com/test/600" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().HaveCount(1);
        run.SubIssueResults[0].Success.Should().BeTrue();

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.EpicApproved,
            "agent:epic-approved must be stripped — it bypasses the decomposition gate");
        // TODO: The two Contain assertions below verify step seeding behaviour (agent:next and
        // agent:generated are always seeded unconditionally) rather than the filtering fix itself.
        // They would pass even against the old (unfixed) code because the proposal only supplies
        // [EpicApproved, "backend"]. The load-bearing assertion is NotContain(EpicApproved) above.
        // Consider removing the redundant Contain assertions or replacing them with a HaveCount(2)
        // check to make the test's intent clearer.
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("backend");
    }

    [Fact]
    public async Task ExecuteAsync_ProposalWithAgentStatusLabels_DropsThemAll()
    {
        // All agent status labels except agent:next and agent:generated must be stripped.
        // TODO: This test covers agent:epic, agent:in-progress, agent:done, agent:error but does NOT
        // assert that agent:cancelled, agent:wont-do, agent:needs-refinement, or agent:epic-approved
        // are also dropped. If any of those labels were mistakenly removed from AgentLabels.All the
        // regression would go undetected. Consider extending to the full AgentLabels.All member set,
        // or converting to a [Theory, MemberData] test parameterised over all agent label strings.
        WriteSubIssueFileWithLabels(
            "01-status.json", "Sub issue with status labels", "Body",
            [AgentLabels.Epic, AgentLabels.InProgress, AgentLabels.Done, AgentLabels.Error, "feature"]);

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "601", Url = "https://github.com/test/601" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain(AgentLabels.Epic);
        capturedLabels.Should().NotContain(AgentLabels.InProgress);
        capturedLabels.Should().NotContain(AgentLabels.Done);
        capturedLabels.Should().NotContain(AgentLabels.Error);
        capturedLabels.Should().Contain(AgentLabels.Next);
        capturedLabels.Should().Contain(AgentLabels.Generated);
        capturedLabels.Should().Contain("feature");
    }

    /// <summary>
    /// When ct is cancelled inside the first provider call (which also returns a success),
    /// CHECK B (part 1) does not discard the success (result.Success is true), so it falls
    /// through to results.Add. CHECK B (part 2) then detects ct and commits the one success
    /// before rethrowing. The second proposal is never attempted.
    ///
    /// This preserves consistency between the issue tracker (where the sub-issue was created)
    /// and the run record (SubIssueResults).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UserCancellationDuringFirstCall_ThrowsOceAndPreservesSuccess()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");
        WriteSubIssueFile("02-second.json", "Second issue", "Body 2");

        using var cts = new CancellationTokenSource();

        // The mock cancels ct then returns success — simulating a race where the provider
        // completed but the outer cancellation was already signalled inside the call.
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(new CreatedIssueResult { Identifier = "500", Url = "https://github.com/test/500" });
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        Func<Task> act = () => step.ExecuteAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // CHECK B fires after call 1 returns; call 2 is never started.
        _issueOps.Verify(
            x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // The successful creation is preserved so the run record stays consistent with the
        // issue tracker.
        run.SubIssueResults.Should().HaveCount(1);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[0].Identifier.Should().Be("500");
        run.DecompositionSubIssuesAttempted.Should().Be(1);
        run.DecompositionSubIssuesCreated.Should().Be(1);
    }

    /// <summary>
    /// When ct is already cancelled before ExecuteAsync is called, CHECK A must fire at the
    /// top of the first foreach iteration, commit an empty results list, and throw
    /// OperationCanceledException before any provider call is made.
    /// Exercises lines 121-124 (CHECK A body).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PreCancelledCt_ThrowsOceBeforeAnyProviderCalls()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");
        WriteSubIssueFile("02-second.json", "Second issue", "Body 2");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel before calling ExecuteAsync

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        Func<Task> act = () => step.ExecuteAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // CHECK A fired at the top of the first iteration; no provider calls were made.
        _issueOps.Verify(
            x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        run.SubIssueResults.Should().BeEmpty();
        run.DecompositionSubIssuesAttempted.Should().Be(0);
        run.DecompositionSubIssuesCreated.Should().Be(0);
    }

    /// <summary>
    /// When a transient provider error occurs on the first attempt and ct is cancelled before
    /// the retry delay, Task.Delay observes the already-cancelled token and throws OCE directly
    /// out of CreateSingleIssueAsync (not via TryCreateIssueOnceAsync's internal catch).
    /// The outer catch(OCE) when(ct.IsCancellationRequested) then commits partial results and
    /// rethrows. Exercises lines 128-139 (the catch block body).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_TransientErrorThenCancellationBeforeRetryDelay_ThrowsOce()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");

        using var cts = new CancellationTokenSource();

        // First call: cancels ct AND throws a transient HTTP error. On the retry path,
        // Task.Delay is called with the already-cancelled creationCt; since the token is
        // already cancelled, Task.Delay throws TaskCanceledException immediately (synchronously),
        // which propagates directly out of CreateSingleIssueAsync to the outer catch block.
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                cts.Cancel();
                throw new HttpRequestException("Server error", null, System.Net.HttpStatusCode.InternalServerError);
            });

        var run = CreateRun();
        var context = BuildContext(run);
        // Non-zero retry delay so Task.Delay(delay, creationCt) is actually called on attempt 2.
        // A non-zero delay with an already-cancelled token throws immediately without sleeping.
        var step = new CreateSubIssuesStep(retryDelayOverride: TimeSpan.FromMilliseconds(1));

        Func<Task> act = () => step.ExecuteAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Only one provider call — the retry delay threw before attempt 2.
        _issueOps.Verify(
            x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        run.SubIssueResults.Should().BeEmpty();
        run.DecompositionSubIssuesAttempted.Should().Be(0);
        run.DecompositionSubIssuesCreated.Should().Be(0);
    }

    /// <summary>
    /// Issue #3876: when ct is cancelled while the second of three proposals is being created,
    /// ExecuteAsync must throw OperationCanceledException, the third proposal must not be
    /// attempted, SubIssueResults must hold exactly one entry (the successful first creation),
    /// and no entry with FailureReason "creation timeout exceeded" must appear.
    ///
    /// This test fails before the fix (ExecuteAsync returns StepResult.Continue, three calls
    /// are made, SubIssueResults has 2 entries with the second attributed as a timeout) and
    /// passes after it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UserCancellationDuringSecondCreation_ThrowsOceAndPreservesFirstResult()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");
        WriteSubIssueFile("02-second.json", "Second issue", "Body 2");
        WriteSubIssueFile("03-third.json", "Third issue", "Body 3");

        using var cts = new CancellationTokenSource();
        var callCount = 0;

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(new CreatedIssueResult { Identifier = "101", Url = "https://example.com/101" });
                // Second call: cancel ct and throw to simulate provider observing the cancellation
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        Func<Task> act = () => step.ExecuteAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Third proposal must never be attempted; create must be called exactly twice.
        _issueOps.Verify(
            x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Only the first success is preserved; no "creation timeout exceeded" misattribution.
        run.SubIssueResults.Should().HaveCount(1);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[0].Identifier.Should().Be("101");
        run.SubIssueResults.Should().NotContain(r => r.FailureReason == "creation timeout exceeded");

        run.DecompositionSubIssuesCreated.Should().Be(1);
        run.DecompositionSubIssuesAttempted.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_SanitizesTitleBeforeCreation()
    {
        // Title with excessive length (newlines can't be in JSON strings directly)
        var longTitle = new string('A', 250);
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = longTitle,
            body = "Body content",
            dependencies = Array.Empty<string>(),
            labels = Array.Empty<string>()
        });
        File.WriteAllText(Path.Combine(dir, "01-long.json"), json);

        string? capturedTitle = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((title, _, _, _) => capturedTitle = title)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "600", Url = "https://github.com/test/600" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        capturedTitle.Should().NotBeNull();
        capturedTitle!.Should().NotContain("\n");
        capturedTitle.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_ContinuesCreatingRemaining()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");
        WriteSubIssueFile("02-second.json", "Second issue", "Body 2");
        WriteSubIssueFile("03-third.json", "Third issue", "Body 3");

        var callCount = 0;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 2)
                    throw new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);
                return Task.FromResult(new CreatedIssueResult { Identifier = $"{700 + callCount}", Url = $"https://github.com/test/{700 + callCount}" });
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().HaveCount(3);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[1].Success.Should().BeFalse();
        run.SubIssueResults[2].Success.Should().BeTrue();
        run.DecompositionSubIssuesCreated.Should().Be(2);
    }

    // ── Issue #3337: case-insensitive label filtering ─────────────────────

    /// <summary>
    /// A proposal that includes a mixed-case gated label (e.g. "Agent:Epic-Approved")
    /// must have that label dropped before the issue is created.
    /// Before the fix: AgentLabels.All.Contains uses ordinal comparison, so
    /// "Agent:Epic-Approved" is not found and is forwarded as a non-agent label.
    /// After the fix: All uses OrdinalIgnoreCase, so the label is correctly dropped.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ProposalWithMixedCaseGatedLabel_DropsThatLabel()
    {
        WriteSubIssueFileWithLabels(
            "01-mixed-case.json", "Sub issue with mixed-case label", "Body",
            ["Agent:Epic-Approved", "backend"]);

        IReadOnlyList<string>? capturedLabels = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, labels, _) => capturedLabels = labels)
            .ReturnsAsync(new CreatedIssueResult { Identifier = "602", Url = "https://github.com/test/602" });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().HaveCount(1);
        run.SubIssueResults[0].Success.Should().BeTrue();

        capturedLabels.Should().NotBeNull();
        capturedLabels.Should().NotContain("Agent:Epic-Approved",
            because: "mixed-case gated labels must be dropped from sub-issue creation");
        capturedLabels.Should().Contain("backend");
        // TODO: This assertion does not constrain the full set of labels — it only checks that the
        // mixed-case gated label is absent and the non-agent label is present. A HaveCount(3) assertion
        // (agent:next + agent:generated + "backend") or explicit NotContain calls for other agent:* labels
        // would close the gap where a future buggy label could slip through undetected alongside "backend".
    }

    // ── MaxDecompositionSubIssues clamp guard — Issue #3624 ───────────────────

    /// <summary>
    /// When MaxDecompositionSubIssues is 0 or negative, the step must clamp the cap to 1
    /// and attempt at least one proposal rather than marking all as SkippedByCap.
    ///
    /// This test fails before the fix (all proposals are SkippedByCap, Attempted == 0)
    /// and passes after it.
    ///
    /// Acceptance criterion 3: "No code path lets a 0 cap reach the proposals.Count > cap
    /// comparison in CreateSubIssuesStep."
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-50)]
    public async Task ExecuteAsync_WhenCapIsNonPositive_ClampsToOneAndAttemptsAtLeastOneProposal(int badCap)
    {
        // Write 3 sub-issue files; with cap=0 and no guard, all would be SkippedByCap
        for (var i = 1; i <= 3; i++)
            WriteSubIssueFile($"{i:D2}-issue-{i}.json", $"Issue {i}", $"Body {i}");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "900", Url = "https://github.com/test/900" });

        var run = CreateRun();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = badCap },
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
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        run.DecompositionSubIssuesAttempted.Should().BeGreaterThanOrEqualTo(1,
            $"cap={badCap} must be clamped to 1; at least one proposal must be attempted");
        run.SubIssueResults.Should().Contain(r => !r.SkippedByCap,
            "at least one result must not be SkippedByCap after clamping");
    }

    /// <summary>
    /// When MaxDecompositionSubIssues is 0 or negative, a Warning must be logged at the step
    /// so the misconfiguration is visible in run output even if the resolver already clamped it.
    ///
    /// Acceptance criterion 2: "A warning is logged when a 0 or negative
    /// MaxDecompositionSubIssues is supplied."
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteAsync_WhenCapIsNonPositive_LogsWarning(int badCap)
    {
        WriteSubIssueFile("01-issue.json", "Issue 1", "Body 1");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "901", Url = "https://github.com/test/901" });

        var mockLogger = new Mock<Serilog.ILogger>();
        // TODO: The ForContext blanket stub returns the same mock for any child logger context.
        // If the step moves the cap-clamp Warning call to a ForContext child logger rather than
        // the injected context.Logger directly, the Verify below would still pass vacuously
        // because all Warning calls on any ForContext child are captured on the same mock.
        // Consider stubbing a separate child mock and verifying on that to make the coupling explicit.
        // Serilog ILogger.ForContext<T>() is called by internal infrastructure; return the mock itself
        // so that calls on the child logger are also captured on the same mock.
        mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
                  .Returns(mockLogger.Object);

        var run = CreateRun();
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = badCap },
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
            PrOrchestrator = new PullRequestOrchestrator(mockLogger.Object),
            Logger = mockLogger.Object
        };
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // TODO: The matcher It.IsAny<string>() is broader than needed — any Warning(string, int) call
        // on the mock satisfies this verify, not just the cap-clamp warning. If a future change adds
        // another Warning(template, someIntValue) call earlier in the step, this test would pass even
        // if the cap-specific warning were removed. Tighten to:
        // It.Is<string>(s => s.Contains("MaxDecompositionSubIssues"))
        // Verify a Warning was logged with the bad cap value (int arg)
        mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<int>()),
            Times.AtLeastOnce,
            $"a Warning must be logged when MaxDecompositionSubIssues={badCap}");
    }
}
