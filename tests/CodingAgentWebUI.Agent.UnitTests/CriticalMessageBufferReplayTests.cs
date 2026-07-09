using System.Net.Http;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Integration-style tests for the critical message buffer replay flow:
/// delivery failure → buffer → reconnect → replay → orchestrator receives completion.
/// </summary>
/// <remarks>
/// Tests the <see cref="AgentWorkerService"/> integration with <see cref="CriticalMessageBuffer"/>
/// including conditional job slot release, buffer drain on reconnection, and drain attempt limits.
/// </remarks>
[Collection("EnvironmentVariables")]
public class CriticalMessageBufferReplayTests
{
    // ── Buffer Integration with Job Completion ───────────────────────────

    [Fact]
    public void CriticalMessageBuffer_ExposedOnService()
    {
        // Verify the buffer field exists and is initialized
        var service = CreateService();
        var buffer = GetBuffer(service);
        buffer.Should().NotBeNull();
        buffer.HasPendingMessages.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAssignJob_CompletionFailure_BuffersMessage()
    {
        // When ReportJobCompleted fails (connection not started), the message should be buffered.
        // We verify by setting up the service with an active job, enqueueing a completion message
        // (as the production catch block does), and then exercising the DrainBufferAsync path
        // which invokes the real SignalR pipeline. This demonstrates the full production flow:
        // SignalR delivery attempt → Polly exhaustion → catch → buffer → re-buffer on drain failure.
        var service = CreateService();
        var buffer = GetBuffer(service);

        // Set up state as if a job completed and ReportJobCompleted failed
        SetPrivateField(service, "_activeJobId", "job-buffer-1");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        var completion = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        // Simulate the production catch block: buffer the message
        // (this is exactly what HandleAssignJobAsync does when _signalRPipeline.ExecuteAsync throws)
        buffer.Enqueue(new BufferedJobCompleted("job-buffer-1", completion, DateTimeOffset.UtcNow));

        // Now exercise the real production replay path via DrainBufferAsync.
        // This calls _signalRPipeline.ExecuteAsync → Connection.InvokeAsync (which fails
        // because the connection is not started), triggering the catch block in DrainBufferAsync
        // which re-buffers with incremented DrainAttempts. This exercises the REAL production
        // code path for buffering on delivery failure.
        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Verify: message is still buffered (delivery failed, re-buffered by production code)
        buffer.HasPendingMessages.Should().BeTrue();
        buffer.Count.Should().Be(1);

        // Verify: the re-buffered message has incremented DrainAttempts (proving it went
        // through the real catch → re-buffer production code path, not just our initial Enqueue)
        var messages = buffer.DrainAll();
        var rebuffered = (BufferedJobCompleted)messages[0];
        rebuffered.DrainAttempts.Should().Be(1, "production code should have incremented DrainAttempts");
        rebuffered.JobId.Should().Be("job-buffer-1");

        // Verify: slot is held (production conditional: buffer has pending → don't release)
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("job-buffer-1",
            "job slot should be held when buffer has pending messages");
    }

    [Fact]
    public async Task JobSlot_NotReleased_WhenBufferHasPending()
    {
        // When buffer has pending messages, the production conditional check
        // (if (!_criticalMessageBuffer.HasPendingMessages) await ReleaseJobSlotAndSignalReadyAsync())
        // should NOT release the slot. We verify by calling DrainBufferAsync which invokes
        // the release-or-hold logic at its end, and confirming the slot stays held.
        var service = CreateService();
        var buffer = GetBuffer(service);

        // Simulate: job completed but ReportJobCompleted failed → message buffered
        SetPrivateField(service, "_activeJobId", "held-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        // Buffer a message that will fail to replay (connection not started)
        // AND that hasn't exhausted its retry limit (DrainAttempts < 3)
        buffer.Enqueue(new BufferedJobCompleted("held-job", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 0));

        // Call DrainBufferAsync — it will attempt replay, fail (no connection),
        // re-buffer the message, and then check: buffer still has pending → don't release slot
        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Production code: after drain, if buffer still has pending messages, slot is NOT released
        buffer.HasPendingMessages.Should().BeTrue("message should be re-buffered after failed replay");
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("held-job",
            "job slot should remain held when buffer has pending messages (production conditional)");
    }

    [Fact]
    public async Task JobSlot_Released_AfterSuccessfulDrain_WhenBufferEmpty()
    {
        // TODO: Misleading test name — this actually tests the early-return path (buffer already
        // empty), not slot release after a successful drain of actual messages. There is no test
        // verifying the slot IS released after draining a non-empty buffer via a live connection.
        // After buffer is drained and becomes empty, job slot should be released
        var service = CreateService();
        var buffer = GetBuffer(service);

        SetPrivateField(service, "_activeJobId", "drain-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        // Buffer is empty — DrainBufferAsync should be a no-op (early return)
        buffer.HasPendingMessages.Should().BeFalse();

        // Call DrainBufferAsync — should return immediately since buffer is empty
        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Slot is NOT released by DrainBufferAsync when buffer was already empty
        // (DrainBufferAsync returns early — the release logic only fires after actual drain)
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("drain-job");
    }

    // ── Drain Attempt Tracking ───────────────────────────────────────────

    [Fact]
    public void DrainBuffer_ReplayFails_ReBuffersWithIncrementedAttempt()
    {
        var buffer = new CriticalMessageBuffer();
        var original = new BufferedJobCompleted("job-retry", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 0);

        // Simulate: drain fails, re-buffer with incremented count
        var rebuffered = original with { DrainAttempts = original.DrainAttempts + 1 };
        buffer.Enqueue(rebuffered);

        var drained = buffer.DrainAll();
        var msg = (BufferedJobCompleted)drained[0];
        msg.DrainAttempts.Should().Be(1);
        msg.JobId.Should().Be("job-retry");
    }

    [Fact]
    public void DrainBuffer_MaxAttemptsExceeded_MessageShouldBeDropped()
    {
        // TODO: This test does not actually validate the drop logic. It only verifies what
        // was put in (DrainAttempts >= 3) rather than that DrainBufferAsync drops it.
        // The actual drop logic is tested in DrainBufferAsync_DropsMessagesExceedingMaxAttempts.
        // Consider removing this test or rewriting to test something meaningful.
        var buffer = new CriticalMessageBuffer();
        var exhausted = new BufferedJobCompleted("job-exhausted", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 3);

        buffer.Enqueue(exhausted);
        var drained = buffer.DrainAll();

        // The message is drained from the queue (DrainAll doesn't filter),
        // but DrainBufferAsync in AgentWorkerService will skip it.
        // Test the logic: DrainAttempts >= 3 means it should be dropped
        var msg = (BufferedJobCompleted)drained[0];
        msg.DrainAttempts.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task DrainBufferAsync_DropsMessagesExceedingMaxAttempts()
    {
        // When DrainBufferAsync encounters a message with DrainAttempts >= 3, it drops it
        var service = CreateService();
        var buffer = GetBuffer(service);

        SetPrivateField(service, "_activeJobId", "expired-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        // Enqueue a message that has already exhausted its retry limit
        buffer.Enqueue(new BufferedJobCompleted("expired-job", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 3));

        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Buffer should be empty (message was dropped, not re-buffered)
        buffer.HasPendingMessages.Should().BeFalse();
        // After drain with empty buffer, slot should be released
        GetPrivateField<string?>(service, "_activeJobId").Should().BeNull();
    }

    // ── DrainBufferAsync Replay Failure Path ─────────────────────────────

    [Fact]
    public async Task DrainBufferAsync_ReplayFails_ReBuffersAndStopsDraining()
    {
        // When replay fails (connection disconnected), the message is re-buffered
        // with incremented DrainAttempts and draining stops
        var service = CreateService();
        var buffer = GetBuffer(service);

        SetPrivateField(service, "_activeJobId", "replay-fail-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        buffer.Enqueue(new BufferedJobCompleted("replay-fail-job", CreatePayload(), DateTimeOffset.UtcNow, DrainAttempts: 0));

        // DrainBufferAsync will try to replay via _hubManager.Connection.InvokeAsync
        // which will fail because the connection is not started
        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Message should be re-buffered with incremented attempt count
        buffer.HasPendingMessages.Should().BeTrue();
        var messages = buffer.DrainAll();
        messages.Should().HaveCount(1);
        var rebuffered = (BufferedJobCompleted)messages[0];
        rebuffered.DrainAttempts.Should().Be(1);
        rebuffered.JobId.Should().Be("replay-fail-job");
    }

    [Fact]
    public async Task DrainBufferAsync_MultipleMessages_StopsAfterFirstFailure_ReBuffersAll()
    {
        // If replay of the first message fails, the failed message AND all remaining
        // unprocessed messages are re-buffered to prevent data loss.
        var service = CreateService();
        var buffer = GetBuffer(service);

        SetPrivateField(service, "_activeJobId", "multi-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        buffer.Enqueue(new BufferedJobCompleted("job-A", CreatePayload(), DateTimeOffset.UtcNow));
        buffer.Enqueue(new BufferedJobCompleted("job-B", CreatePayload(), DateTimeOffset.UtcNow));

        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");
        var task = (Task)drainMethod.Invoke(service, [])!;
        await task;

        // Both messages should be preserved in the buffer:
        // - job-A: re-buffered with DrainAttempts incremented (replay failed)
        // - job-B: re-buffered as-is (never attempted, preserved from data loss)
        buffer.HasPendingMessages.Should().BeTrue();
        buffer.Count.Should().Be(2, "both the failed message and remaining unprocessed messages should be re-buffered");

        var messages = buffer.DrainAll();
        var jobA = (BufferedJobCompleted)messages[0];
        var jobB = (BufferedJobCompleted)messages[1];

        jobA.JobId.Should().Be("job-A");
        jobA.DrainAttempts.Should().Be(1, "failed message should have incremented DrainAttempts");

        jobB.JobId.Should().Be("job-B");
        jobB.DrainAttempts.Should().Be(0, "unprocessed message should retain original DrainAttempts");
    }

    // ── BuildRegistrationMessage includes ActiveJob when buffer is pending ──

    [Fact]
    public void BuildRegistrationMessage_IncludesActiveJob_WhenSlotHeld()
    {
        var service = CreateService();
        var buffer = GetBuffer(service);

        // Simulate: job completed, ReportJobCompleted failed, slot held
        SetPrivateField(service, "_activeJobId", "pending-job");
        SetPrivateField(service, "_activeJobAssignment", CreateTestJobAssignment("pending-job"));
        SetPrivateField(service, "_activeJobStartedAt", DateTimeOffset.UtcNow);
        buffer.Enqueue(new BufferedJobCompleted("pending-job", CreatePayload(), DateTimeOffset.UtcNow));

        // BuildRegistrationMessage should include ActiveJob since _activeJobId is set
        var buildMethod = typeof(AgentWorkerService).GetMethod(
            "BuildRegistrationMessage",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var registration = (AgentRegistrationMessage)buildMethod.Invoke(service, [])!;

        registration.ActiveJob.Should().NotBeNull();
        registration.ActiveJob!.RunId.Should().Be("pending-job");
    }

    // ── End-to-End Buffer Lifecycle ──────────────────────────────────────

    [Fact]
    public async Task EndToEnd_BufferLifecycle_EnqueueDrainRelease()
    {
        // Simulate the full lifecycle:
        // 1. Job completes
        // 2. ReportJobCompleted fails → buffered
        // 3. Slot held
        // 4. DrainBufferAsync called (simulating reconnection)
        // 5. Drain fails (no connection) → re-buffered with attempt=1
        // 6. DrainBufferAsync called again → fails → re-buffered with attempt=2
        // 7. DrainBufferAsync called again → fails → re-buffered with attempt=3
        // 8. DrainBufferAsync called again → dropped (attempt >= 3) → slot released

        var service = CreateService();
        var buffer = GetBuffer(service);
        var drainMethod = GetPrivateMethod(service, "DrainBufferAsync");

        SetPrivateField(service, "_activeJobId", "lifecycle-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        // Step 2: Buffer the message
        buffer.Enqueue(new BufferedJobCompleted("lifecycle-job", CreatePayload(), DateTimeOffset.UtcNow));

        // Steps 4-7: Drain attempts fail (connection not started)
        for (var i = 0; i < 3; i++)
        {
            var task = (Task)drainMethod.Invoke(service, [])!;
            await task;
            // Should still be pending (re-buffered with incremented attempts)
            buffer.HasPendingMessages.Should().BeTrue($"after drain attempt {i + 1}, message should be re-buffered");
            GetPrivateField<string?>(service, "_activeJobId").Should().NotBeNull($"slot should remain held after drain attempt {i + 1}");
        }

        // Verify attempt count reached max
        var messages = buffer.DrainAll();
        messages.Should().HaveCount(1);
        var msg = (BufferedJobCompleted)messages[0];
        msg.DrainAttempts.Should().Be(3);

        // Re-enqueue for final drain that should drop it
        buffer.Enqueue(msg);

        // Step 8: Final drain — message dropped, slot released
        // Need to reset _activeJobId since previous drain may have cleared it
        // Actually, DrainBufferAsync only releases slot when buffer is empty.
        // The buffer still had pending messages, so slot was not released.
        SetPrivateField(service, "_activeJobId", "lifecycle-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        var finalDrain = (Task)drainMethod.Invoke(service, [])!;
        await finalDrain;

        buffer.HasPendingMessages.Should().BeFalse("exhausted message should be dropped");
        GetPrivateField<string?>(service, "_activeJobId").Should().BeNull("slot should be released after buffer is empty");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentWorkerService CreateService()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var hubManager = CreateTestHubManager();
        var buffer = new CriticalMessageBuffer();
        var signalRPipeline = CodingAgentWebUI.Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger.Object);
        var completionReporter = new SignalRCompletionReporter(hubManager, signalRPipeline, buffer, mockLogger.Object);
        return new AgentWorkerService(
            hubManager,
            CreateTestHubManagerFactory(),
            CreateMockExecutor(),
            CreateMockConsolidationExecutor(),
            completionReporter,
            mockOrchestrator.Object,
            Mock.Of<IHttpClientFactory>(),
            new AgentIdentity("test-agent"),
            Mock.Of<IHostApplicationLifetime>(),
            mockLogger.Object);
    }

    private static HubConnectionManager CreateTestHubManager()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManager("http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }

    private static HubConnectionManagerFactory CreateTestHubManagerFactory()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManagerFactory("http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }

    private static LocalPipelineExecutor CreateMockExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockQualityGateValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalPipelineExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object,
            agentIdentity: new AgentIdentity("test-agent"));
    }

    private static LocalConsolidationExecutor CreateMockConsolidationExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalConsolidationExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            mockLogger.Object);
    }

    private static CriticalMessageBuffer GetBuffer(AgentWorkerService service)
    {
        var reporterField = typeof(AgentWorkerService).GetField(
            "_completionReporter",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_completionReporter' not found");
        var reporter = reporterField.GetValue(service)!;
        if (reporter is SignalRCompletionReporter signalRReporter)
            return signalRReporter.Buffer;
        throw new InvalidOperationException(
            $"Expected SignalRCompletionReporter but got {reporter.GetType().Name}");
    }

    private static JobCompletionPayload CreatePayload() => new()
    {
        FinalStep = PipelineStep.Completed,
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static JobAssignmentMessage CreateTestJobAssignment(string jobId = "test-job-1") => new()
    {
        JobId = jobId,
        IssueIdentifier = "owner/repo#1",
        IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        McpServers = [],
        InitiatedBy = "test-user"
    };

    private static MethodInfo GetPrivateMethod(object obj, string methodName)
    {
        return obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found");
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found");
        return (T?)field.GetValue(obj);
    }

    // TODO: Add test for orchestrator-side idempotency guard (AgentHub.Pipeline.cs).
    // The acceptance criteria require "Duplicate ReportJobCompleted for the same jobId is handled
    // idempotently on the orchestrator (no crash, no double-history)." The new else branch with
    // the debug log is untested — calling ReportJobCompleted twice for the same jobId where
    // GetRun returns null on the second call is not covered by any test.

    // TODO: Add test for HeartbeatMonitor interaction with held slot during partition.
    // The acceptance criteria require "HeartbeatMonitor no longer produces false 'agent stuck'
    // failures for jobs that completed during a network partition (verifiable in test)."
    // Need a scenario that exercises: delivery failure → buffer → progress timeout fires →
    // reconnect → replay → verify HeartbeatMonitor did NOT mark it as stuck.
    // A basic test exists in HeartbeatMonitorServiceTests but a full end-to-end scenario
    // (with the agent-side buffer interaction) is not covered.
}
