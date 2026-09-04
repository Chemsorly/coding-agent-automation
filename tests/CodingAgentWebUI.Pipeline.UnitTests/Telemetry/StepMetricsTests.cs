using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

public class StepMetricsTests : IDisposable
{
    private readonly TestMeterFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private System.Diagnostics.Metrics.Meter CreateMeter() =>
        _factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));

    [Fact]
    public async Task PipelineStepRunner_EmitsStepDurationAndCount()
    {
        var meter = CreateMeter();
        using var histCollector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "pipeline.step.duration");
        using var countCollector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.step.count");

        var step = new FakeStep("TestStep", StepResult.Continue);
        var context = BuildContext();

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None, meter);

        histCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        var hist = histCollector.GetMeasurementSnapshot().First(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        countCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Value == 1 && m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "TestStep")));
    }

    [Fact]
    public async Task PipelineStepRunner_EmitsMetricsOnStop()
    {
        var meter = CreateMeter();
        using var histCollector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "pipeline.step.duration");
        using var countCollector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.step.count");

        var step = new FakeStep("StopStep", StepResult.Stop);
        var context = BuildContext();

        await PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None, meter);

        histCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "StopStep")));
        countCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "StopStep")));
    }

    [Fact]
    public async Task PipelineStepRunner_EmitsMetricsOnException()
    {
        var meter = CreateMeter();
        using var histCollector = new MetricCollector<double>(_factory, PipelineTelemetry.SourceName, "pipeline.step.duration");
        using var countCollector = new MetricCollector<long>(_factory, PipelineTelemetry.SourceName, "pipeline.step.count");

        var step = new ThrowingStep("FailStep");
        var context = BuildContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None, meter));

        histCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FailStep")));
        countCollector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FailStep")));
    }

    [Fact]
    public void AccumulateTokenUsage_EmitsTokensUsedCounter()
    {
        // AccumulateTokenUsage calls the static PipelineTelemetry counters — assert with MeterListener
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var counters = new System.Collections.Concurrent.ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            counters.Add((instrument.Name, measurement, tags.ToArray())));
        listener.Start();

        var run = CreateRun(PipelineRunType.Implementation, "proj-1", "TestProj");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };

        run.AccumulateTokenUsage(result);
        listener.Dispose();

        var counter = counters.Should().Contain(c => c.Name == "agent.tokens.used"
            && c.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "proj-1")))
            .Which;
        counter.Value.Should().Be(150);
        counter.Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
        counter.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "TestProj"));
    }

    [Fact]
    public void AccumulateTokenUsage_NullResult_DoesNotEmit()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var counters = new System.Collections.Concurrent.ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (tags.ToArray().Contains(new KeyValuePair<string, object?>("pipeline.project_id", "null-result-test")))
                counters.Add(instrument.Name);
        });
        listener.Start();

        var run = CreateRun(projectId: "null-result-test");
        run.AccumulateTokenUsage(null);
        listener.Dispose();

        counters.Should().NotContain("agent.tokens.used");
    }

    [Fact]
    public void AccumulateTokenUsage_NullUsage_DoesNotEmit()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var counters = new System.Collections.Concurrent.ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (tags.ToArray().Contains(new KeyValuePair<string, object?>("pipeline.project_id", "null-usage-test")))
                counters.Add(instrument.Name);
        });
        listener.Start();

        var run = CreateRun(projectId: "null-usage-test");
        var result = new AgentResult { ExitCode = 0, OutputLines = [], Usage = null };
        run.AccumulateTokenUsage(result);
        listener.Dispose();

        counters.Should().NotContain("agent.tokens.used");
    }

    [Fact]
    public void AccumulateTokenUsage_WithCost_EmitsCostUsdCounter()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var histograms = new System.Collections.Concurrent.ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            histograms.Add((instrument.Name, measurement, tags.ToArray())));
        listener.Start();

        var run = CreateRun(PipelineRunType.Implementation, "proj-cost-1", "TestProj");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = 0.05m
        };

        run.AccumulateTokenUsage(result);
        listener.Dispose();

        var metric = histograms.Should().Contain(h => h.Name == "agent.cost.usd"
            && h.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "proj-cost-1")))
            .Which;
        metric.Value.Should().Be(0.05);
        metric.Tags.Should().Contain(new KeyValuePair<string, object?>("run_type", "implementation"));
        metric.Tags.Should().Contain(new KeyValuePair<string, object?>("pipeline.project_name", "TestProj"));
    }

    [Fact]
    public void AccumulateTokenUsage_NullCost_DoesNotEmitCostUsd()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var histograms = new System.Collections.Concurrent.ConcurrentBag<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (tags.ToArray().Contains(new KeyValuePair<string, object?>("pipeline.project_id", "null-cost-test")))
                histograms.Add(instrument.Name);
        });
        listener.Start();

        var run = CreateRun(PipelineRunType.Implementation, "null-cost-test", "TestProj");
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
            Cost = null
        };

        run.AccumulateTokenUsage(result);
        listener.Dispose();

        histograms.Should().NotContain("agent.cost.usd");
    }

    [Fact]
    public void BuildStepTags_IncludesAllExpectedTags()
    {
        var run = CreateRun(PipelineRunType.Review, "p1", "Proj");

        var tags = PipelineTelemetry.BuildStepTags("MyStep", run.RunType, run.ProjectId, run.ProjectName);

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
