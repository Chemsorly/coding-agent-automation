using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

public class StepMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _histograms = [];
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];

    public StepMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            _histograms.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task PipelineStepRunner_EmitsStepDurationAndCount()
    {
        var step = new FakeStep("TestStep", StepResult.Continue);
        var context = BuildContext();

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        _histograms.Should().Contain(h => h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        var hist = _histograms.First(h => h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        _counters.Should().Contain(c => c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        var counter = _counters.First(c => c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        counter.Value.Should().Be(1);
    }

    [Fact]
    public async Task PipelineStepRunner_EmitsMetricsOnStop()
    {
        var step = new FakeStep("StopStep", StepResult.Stop);
        var context = BuildContext();

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None);

        _histograms.Should().Contain(h => h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "StopStep")));
        _counters.Should().Contain(c => c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "StopStep")));
    }

    [Fact]
    public async Task PipelineStepRunner_EmitsMetricsOnException()
    {
        var step = new ThrowingStep("FailStep");
        var context = BuildContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None));

        _histograms.Should().Contain(h => h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FailStep")));
        _counters.Should().Contain(c => c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FailStep")));
    }

    [Fact]
    public void AccumulateTokenUsage_EmitsTokensUsedCounter()
    {
        var run = CreateRun(PipelineRunType.Implementation, "proj-1", "TestProj");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };

        run.AccumulateTokenUsage(result);

        _counters.Should().ContainSingle(c => c.Name == "agent.tokens.used");
        var counter = _counters.First(c => c.Name == "agent.tokens.used");
        counter.Value.Should().Be(150);
        counter.Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
        counter.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "proj-1"));
        counter.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "TestProj"));
    }

    [Fact]
    public void AccumulateTokenUsage_NullResult_DoesNotEmit()
    {
        var run = CreateRun();
        run.AccumulateTokenUsage(null);
        _counters.Should().NotContain(c => c.Name == "agent.tokens.used");
    }

    [Fact]
    public void AccumulateTokenUsage_NullUsage_DoesNotEmit()
    {
        var run = CreateRun();
        var result = new AgentResult { ExitCode = 0, OutputLines = [], Usage = null };
        run.AccumulateTokenUsage(result);
        _counters.Should().NotContain(c => c.Name == "agent.tokens.used");
    }

    [Fact]
    public void BuildStepTags_IncludesAllExpectedTags()
    {
        var run = CreateRun(PipelineRunType.Review, "p1", "Proj");

        var tags = PipelineTelemetry.BuildStepTags("MyStep", run);

        var tagList = new List<KeyValuePair<string, object?>>();
        foreach (var tag in tags)
            tagList.Add(tag);

        tagList.Should().Contain(new KeyValuePair<string, object?>("step_name", "MyStep"));
        tagList.Should().Contain(new KeyValuePair<string, object?>("run_type", "review"));
        tagList.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_id", "p1"));
        tagList.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "Proj"));
    }

    private static PipelineRun CreateRun(
        PipelineRunType runType = PipelineRunType.Implementation,
        string? projectId = null,
        string? projectName = null) => new()
    {
        RunId = "test-run",
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = runType,
        ProjectId = projectId,
        ProjectName = projectName
    };

    private static PipelineStepContext BuildContext()
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var run = CreateRun(PipelineRunType.Implementation, "proj", "TestProject");
        var prOrchestrator = new PullRequestOrchestrator(logger);
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            IssueProvider = Mock.Of<IIssueProvider>(),
            Callbacks = Mock.Of<IPipelineCallbacks>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = new AgentPhaseExecutor(logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, new CiLogWriter(logger), new FeedbackService(logger), logger),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = logger
        };
    }

    private sealed class FakeStep(string name, StepResult result) : IPipelineStep
    {
        public string StepName => name;
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingStep(string name) : IPipelineStep
    {
        public string StepName => name;
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct) =>
            throw new InvalidOperationException("Step failed");
    }
}
