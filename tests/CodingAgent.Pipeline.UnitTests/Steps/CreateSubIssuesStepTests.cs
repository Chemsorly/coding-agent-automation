using AwesomeAssertions;
using System.Diagnostics.Metrics;
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
// Added to [Collection("Metrics")] because ExecuteAsync_SuccessfulCreation_IncrementsSubIssuesCreatedCounter
// uses a MeterListener against the static PipelineTelemetry.SubIssuesCreated counter. Without
// serialization, other test classes that also emit on the same static meter can bleed into this
// class's MeterListener callback, causing snapshot-delta assertions to see inflated counts (delta=2
// instead of 1). The "Metrics" collection serializes all metric-listener tests process-wide.
[Collection("Metrics")]
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

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_MarksRemainingAsFailed()
    {
        WriteSubIssueFile("01-first.json", "First issue", "Body 1");
        WriteSubIssueFile("02-second.json", "Second issue", "Body 2");

        using var cts = new CancellationTokenSource();

        var callCount = 0;
        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, IReadOnlyList<string>, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call succeeds, then cancel
                    cts.Cancel();
                    return Task.FromResult(new CreatedIssueResult { Identifier = "500", Url = "https://github.com/test/500" });
                }
                throw new OperationCanceledException();
            });

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        var result = await step.ExecuteAsync(context, cts.Token);

        result.Should().Be(StepResult.Continue);
        // First should succeed, second should fail due to cancellation/timeout
        run.SubIssueResults.Should().HaveCount(2);
        run.SubIssueResults[0].Success.Should().BeTrue();
        run.SubIssueResults[1].Success.Should().BeFalse();
        run.SubIssueResults[1].FailureReason.Should().Contain("timeout");
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

    [Fact]
    public async Task ExecuteAsync_SuccessfulCreation_IncrementsSubIssuesCreatedCounter()
    {
        WriteSubIssueFile("01-feature.json", "Add feature", "Feature body");

        _issueOps.Setup(x => x.CreateIssueAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "900", Url = "https://github.com/test/900" });

        long createdCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "pipeline.decomposition.sub_issues.created")
                Interlocked.Add(ref createdCount, measurement);
        });
        listener.Start();

        // Capture baseline after Start() so only events received while the listener is active
        // are counted. Any measurements fired before Start() are not delivered to this listener.
        var baseline = Interlocked.Read(ref createdCount);

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        // Dispose the listener immediately after ExecuteAsync returns to stop receiving events
        // from any concurrent tests that fire the same counter, preventing the delta from being
        // inflated by parallel test activity.
        listener.Dispose();

        var delta = Interlocked.Read(ref createdCount) - baseline;
        delta.Should().Be(1);
    }
}
