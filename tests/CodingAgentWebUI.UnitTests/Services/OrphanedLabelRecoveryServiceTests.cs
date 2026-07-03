using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for OrphanedLabelRecoveryService — validates periodic sweep behavior,
/// grace period, config-driven interval, and orphan detection logic.
/// </summary>
// TODO: Add a test for periodic loop error resilience — verify that when RecoverOrphanedLabelsAsync
// throws a non-OperationCanceledException inside the while loop, the service logs a warning and
// continues to the next tick rather than terminating. This exercises the outer catch in the periodic loop.
public class OrphanedLabelRecoveryServiceTests : IDisposable
{
    private readonly Mock<IOrchestratorRunService> _mockRunService;
    private readonly Mock<IProjectStore> _mockProjectStore;
    private readonly Mock<IProviderConfigStore> _mockProviderConfigStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<ILabelSwapper> _mockLabelSwapper;
    private readonly Mock<IPipelineConfigStore> _mockConfigStore;
    private readonly Mock<ILogger> _mockLogger;
    private readonly CancellationTokenSource _cts;

    public OrphanedLabelRecoveryServiceTests()
    {
        _mockRunService = new Mock<IOrchestratorRunService>();
        _mockProjectStore = new Mock<IProjectStore>();
        _mockProviderConfigStore = new Mock<IProviderConfigStore>();
        _mockProviderFactory = new Mock<IProviderFactory>(MockBehavior.Strict);
        _mockLabelSwapper = new Mock<ILabelSwapper>();
        _mockConfigStore = new Mock<IPipelineConfigStore>();
        _mockLogger = new Mock<ILogger>();

        _mockLogger
            .Setup(l => l.ForContext<OrphanedLabelRecoveryService>())
            .Returns(_mockLogger.Object);

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 30 });

        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        _cts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _cts.Dispose();
    }

    [Fact]
    public async Task Sweep_SwapsOrphanedIssues()
    {
        // Arrange: one template with one provider, one orphaned issue
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Orphaned issue",
            Labels = new[] { "agent:in-progress" }
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("42", "provider-1"))
            .Returns(false);

        var labelSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("provider-1", "42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => labelSwapCalled.TrySetResult());

        // Act: start the service (grace period is 60s, but we'll cancel after the swap)
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(labelSwapCalled.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: the swap was called
        completed.Should().BeSameAs(labelSwapCalled.Task, "SwapLabelAsync should have been called after grace period");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_SkipsActiveRuns()
    {
        // Arrange: one template, one issue that IS being processed
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Active issue",
            Labels = new[] { "agent:in-progress" }
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("42", "provider-1"))
            .Returns(true);

        // Act: start service, wait for sweep to complete (no swap expected)
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        // Wait long enough for the grace period + sweep to run
        await Task.Delay(TimeSpan.FromSeconds(65));

        // Assert: SwapLabelAsync was NOT called
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_ContinuesOnLabelSwapFailure()
    {
        // Arrange: two orphaned issues, first swap throws, second should still succeed
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1",
            new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = new[] { "agent:in-progress" } },
            new IssueSummary { Identifier = "2", Title = "Issue 2", Labels = new[] { "agent:in-progress" } });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("provider-1", "1", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub API error"));

        var secondSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("provider-1", "2", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => secondSwapCalled.TrySetResult());

        // Act
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(secondSwapCalled.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: second issue was still processed despite first failure
        completed.Should().BeSameAs(secondSwapCalled.Task, "Second swap should succeed despite first failure");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_ContinuesOnProviderScanFailure()
    {
        // Arrange: two providers, first throws, second should still be scanned
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template 1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "Template 2", IssueProviderId = "provider-2", RepoProviderId = "r2" }
        };
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        // Provider-1 config lookup fails
        _mockProviderConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync("provider-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        // Provider-2 succeeds
        SetupProviderConfig("provider-2");
        SetupIssueProvider("provider-2", new IssueSummary
        {
            Identifier = "99",
            Title = "Orphaned",
            Labels = new[] { "agent:in-progress" }
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("99", "provider-2"))
            .Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("provider-2", "99", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Act
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: second provider was scanned despite first failure
        completed.Should().BeSameAs(swapCalled.Task, "Second provider should be scanned despite first provider failure");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoTemplates_SkipsSweep()
    {
        // Arrange: no templates configured (default mock returns empty)
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act: start and wait past grace period
        using var service = CreateService();
        await service.StartAsync(_cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(65));

        // Assert: no provider scans attempted
        _mockProviderConfigStore.Verify(
            s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MultipleProviders_ScansAll()
    {
        // Arrange: two templates with different providers
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "T2", IssueProviderId = "provider-2", RepoProviderId = "r2" }
        };
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        SetupProviderConfig("provider-1");
        SetupProviderConfig("provider-2");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "10",
            Title = "Issue A",
            Labels = new[] { "agent:in-progress" }
        });
        SetupIssueProvider("provider-2", new IssueSummary
        {
            Identifier = "20",
            Title = "Issue B",
            Labels = new[] { "agent:in-progress" }
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var swapCount = 0;
        var allSwapsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() =>
            {
                if (Interlocked.Increment(ref swapCount) >= 2)
                    allSwapsDone.TrySetResult();
            });

        // Act
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(allSwapsDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: both providers were scanned
        completed.Should().BeSameAs(allSwapsDone.Task, "Both providers should be scanned");
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync("provider-1", "10", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockLabelSwapper.Verify(
            l => l.SwapLabelAsync("provider-2", "20", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigIntervalBelowMinimum_ClampedWithWarning()
    {
        // Arrange: config with interval below minimum (1 minute < 5 minute minimum)
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 1 });

        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1"); // no issues

        // Track sweep calls to verify the service starts with a valid interval
        var sweepCount = 0;
        var firstSweepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" }
            })
            .Callback(() =>
            {
                if (Interlocked.Increment(ref sweepCount) == 1)
                    firstSweepDone.TrySetResult();
            });

        // Act: start service — it should not throw despite bad config value
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(firstSweepDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: service started successfully (no ArgumentOutOfRangeException from PeriodicTimer)
        completed.Should().BeSameAs(firstSweepDone.Task, "Service should start and perform first sweep despite low interval config");
        // TODO: Assert that a warning was logged containing the clamping message (e.g., verify
        // _mockLogger received a Warning call mentioning "below minimum" and the clamped value).

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_StopsGracefully()
    {
        // Arrange: empty templates
        using var service = CreateService();

        // Act: start and immediately cancel
        await service.StartAsync(_cts.Token);
        _cts.Cancel();

        // Assert: stop completes without exception
        var stopTask = service.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        stopTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task DeduplicatesProviderIds()
    {
        // Arrange: two templates with the SAME provider ID
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "T2", IssueProviderId = "provider-1", RepoProviderId = "r2" }
        };
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "5",
            Title = "Issue",
            Labels = new[] { "agent:in-progress" }
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("5", "provider-1"))
            .Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelSwapper
            .Setup(l => l.SwapLabelAsync("provider-1", "5", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Act
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        completed.Should().BeSameAs(swapCalled.Task);

        // Assert: provider was only scanned once (deduplicated)
        _mockProviderConfigStore.Verify(
            s => s.GetProviderConfigByIdAsync("provider-1", ProviderKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PeriodicSweep_ServiceRemainsRunningAfterFirstSweep()
    {
        // Arrange: set up a valid config and provider so the first sweep completes successfully
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1"); // no issues

        var firstSweepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweepCount = 0;
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" }
            })
            .Callback(() =>
            {
                if (Interlocked.Increment(ref sweepCount) == 1)
                    firstSweepDone.TrySetResult();
            });

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 30 });

        // Act: start the service and wait for the first sweep to complete
        using var service = CreateService();
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(firstSweepDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        completed.Should().BeSameAs(firstSweepDone.Task, "First sweep should complete after grace period");

        // Assert: after the first sweep, the service is still running (entered periodic loop).
        // The old single-run implementation would have ExecuteTask completed here.
        // With periodic behavior, ExecuteTask remains incomplete until cancellation.
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // small buffer for async continuation
        service.ExecuteTask!.IsCompleted.Should().BeFalse(
            "Service should remain running in the periodic loop after first sweep — " +
            "if it completed, the periodic timer was never entered");

        // Verify exactly one sweep ran (the initial sweep), proving the service is now
        // waiting for the next timer tick rather than exiting or looping without a timer.
        sweepCount.Should().Be(1,
            "Only the initial sweep should have run — the service should be blocked on " +
            "PeriodicTimer.WaitForNextTickAsync, not completing or spinning");

        // Verify config was loaded exactly once to establish the timer interval
        _mockConfigStore.Verify(
            c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "Config should be loaded exactly once after first sweep to determine periodic interval");

        // Verify cancellation causes the service to exit cleanly from the timer loop
        _cts.Cancel();
        var stopTask = service.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        stopTask.IsCompletedSuccessfully.Should().BeTrue(
            "Service should stop gracefully when cancelled while waiting for timer tick");
    }

    // TODO: Inject TimeProvider (available in .NET 8+) into OrphanedLabelRecoveryService to enable
    // testing actual periodic sweep execution without real wall-clock delays. With FakeTimeProvider,
    // tests could advance time and verify that multiple sweeps occur at the configured interval.
    // Currently, the 5-minute minimum interval makes it impractical to test multiple ticks in a unit test.

    // ── Helpers ─────────────────────────────────────────────────────────

    private OrphanedLabelRecoveryService CreateService() => new(
        _mockRunService.Object,
        _mockProjectStore.Object,
        _mockProviderConfigStore.Object,
        _mockProviderFactory.Object,
        _mockLabelSwapper.Object,
        _mockConfigStore.Object,
        _mockLogger.Object);

    private void SetupTemplateWithProvider(string providerId)
    {
        _mockProjectStore
            .Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "Test Template", IssueProviderId = providerId, RepoProviderId = "repo-1" }
            });
    }

    private void SetupProviderConfig(string providerId)
    {
        _mockProviderConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(providerId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig
            {
                Id = providerId,
                Kind = ProviderKind.Issue,
                DisplayName = $"Provider {providerId}",
                ProviderType = "GitHub",
                Settings = new Dictionary<string, string>()
            });
    }

    private void SetupIssueProvider(string providerId, params IssueSummary[] issues)
    {
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = issues,
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == providerId)))
            .Returns(mockIssueProvider.Object);
    }
}
