using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.Services;
using Moq;

namespace CodingAgent.Web.UnitTests.Telemetry;

/// <summary>
/// Observability tests for <see cref="ConsolidationDispatcher"/>:
/// verifies that the <see cref="PipelineTelemetry.ConsolidationDispatchPermanentFailures"/>
/// counter is incremented (with the correct run.type tag) when a permanent dispatch failure
/// cascades the run to Failed.
///
/// Uses a scoped <see cref="MeterListener"/> (disposed inside each test) to observe the global
/// static <see cref="PipelineTelemetry.Meter"/>. Placed in [Collection("Metrics")] to prevent
/// cross-talk with <see cref="ConsolidationDispatcherTests"/>, which also exercises permanent
/// failure paths that emit measurements on the same global counter.
/// </summary>
[Collection("Metrics")]
public sealed class ConsolidationDispatcherObservabilityTests
{
    private readonly Mock<IWorkDistributor> _workDistributor = new();
    private readonly Mock<IAgentProfileStore> _profileStore = new();
    private readonly Mock<IConsolidationWorkspaceManager> _workspaceManager = new();
    private readonly Mock<IPipelineConfigStore> _configStore = new();
    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IProjectStore> _projectStore = new();

    private ConsolidationDispatcher CreateSut() => new(
        _workDistributor.Object,
        _profileStore.Object,
        _workspaceManager.Object,
        _configStore.Object,
        _consolidationService.Object,
        _projectStore.Object);

    private void SetupDefaults()
    {
        _configStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        _profileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentProfile
                {
                    Id = "default-profile",
                    DisplayName = "Default",
                    Enabled = true,
                    Priority = 0,
                    MatchLabels = ["kiro", "dotnet", "dotnet10"],
                    AgentProviderConfigId = "provider-1"
                }
            });

        _workspaceManager
            .Setup(m => m.GetWorkspacePath(It.IsAny<RunId>()))
            .Returns("/workspaces/test");

        _projectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        _consolidationService
            .Setup(s => s.UpdateRunAsync(
                It.IsAny<RunId>(), It.IsAny<ConsolidationRunStatus>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// When DistributeAsync returns IsPermanentFailure=true, the
    /// <see cref="PipelineTelemetry.ConsolidationDispatchPermanentFailures"/> counter must
    /// be incremented exactly once with a run.type tag matching the run's type.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_PermanentFailure_IncrementsCounter_WithRunTypeTag()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null,
                "No job template for agent selector 'kiro,python,python312': 422 Unprocessable Entity",
                IsPermanentFailure: true));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var measurements = new List<(long Value, string RunType)>();
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "consolidation.dispatch.permanent_failures")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "consolidation.dispatch.permanent_failures") return;
            var runType = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run.type") runType = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, runType));
        });
        meter.Start();

        try
        {
            var sut = CreateSut();
            await sut.DispatchRunAsync(run, CancellationToken.None);
        }
        finally
        {
            meter.Dispose();
        }

        measurements.Should().ContainSingle(
            "a single permanent failure must increment the counter exactly once");
        measurements[0].Value.Should().Be(1,
            "each permanent failure increments the counter by 1");
        measurements[0].RunType.Should().Be(ConsolidationRunType.BrainConsolidation.ToString(),
            "run.type tag must match the run's ConsolidationRunType");
    }

    /// <summary>
    /// When DistributeAsync returns a transient failure (IsPermanentFailure=false), the
    /// <see cref="PipelineTelemetry.ConsolidationDispatchPermanentFailures"/> counter must
    /// NOT be incremented — transient failures leave the run Queued for retry.
    /// </summary>
    [Fact]
    public async Task DispatchRunAsync_TransientFailure_DoesNotIncrementCounter()
    {
        SetupDefaults();
        _workDistributor
            .Setup(d => d.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(false, null,
                "No capacity (409): Concurrency limit reached",
                IsPermanentFailure: false));

        var run = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        var measurementCount = 0;
        using var meter = new MeterListener();
        meter.InstrumentPublished += (instrument, listener) =>
        {
            if (instrument.Name == "consolidation.dispatch.permanent_failures")
                listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "consolidation.dispatch.permanent_failures")
                measurementCount++;
        });
        meter.Start();

        try
        {
            var sut = CreateSut();
            await sut.DispatchRunAsync(run, CancellationToken.None);
        }
        finally
        {
            meter.Dispose();
        }

        measurementCount.Should().Be(0,
            "transient failures must not increment the permanent_failures counter");
    }
}
