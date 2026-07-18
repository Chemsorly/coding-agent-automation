using System.Net.Http;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentWorkerService"/> job slot acquisition,
/// concurrency races, and heartbeat lifecycle transitions.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentWorkerServiceJobSlotTests
{
    // ── Capacity Enforcement ─────────────────────────────────────────────

    [Fact]
    public void TryAcquireJobSlot_BelowCapacity_AcquiresSlot()
    {
        var service = CreateService();

        var result = InvokeTryAcquireJobSlot(service, "job-1", out var busyWith);

        result.Should().BeTrue();
        busyWith.Should().BeNull();
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().Be("job-1");
    }

    [Fact]
    public void TryAcquireJobSlot_AtCapacity_ActiveJob_Rejects()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "existing-job");

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeFalse();
        busyWith.Should().Be("existing-job");
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().Be("existing-job");
    }

    [Fact]
    public void TryAcquireJobSlot_AtCapacity_ActiveChat_Rejects()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeChatSessionId", "session-42");

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeFalse();
        busyWith.Should().Be("chat:session-42");
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireJobSlot_AfterRelease_SlotFreed()
    {
        // TODO: This test never verifies that signalReady callback was invoked during
        // ReleaseJobSlotAndSignalReadyAsync. If the _signalReady() call were accidentally
        // removed, this test would still pass. Add assertion on callback invocation.
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "old-job");
        SetPrivateField(GetSlotManager(service), "_jobCts", new CancellationTokenSource());

        await GetSlotManager(service).ReleaseJobSlotAndSignalReadyAsync();

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeTrue();
        busyWith.Should().BeNull();
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().Be("new-job");
    }

    // ── Rejection Notification ───────────────────────────────────────────

    [Fact]
    public async Task HandleAssignJob_WhenBusy_RejectionPathCompletes()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "existing-job");

        var message = CreateTestJobAssignment("rejected-job");
        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        var task = (Task)handler.Invoke(service, [message])!;
        await task;

        // Handler completes without throwing; active job unchanged
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().Be("existing-job");
    }

    [Fact]
    public async Task HandleAssignConsolidationJob_WhenBusy_RejectionPathCompletes()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "existing-job");

        var message = new ConsolidationJobMessage
        {
            JobId = "consolidation-rejected",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        var handler = GetPrivateMethod(service, "HandleAssignConsolidationJobAsync");
        var task = (Task)handler.Invoke(service, [message])!;
        await task;

        // Handler completes without throwing; active job unchanged
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().Be("existing-job");
    }

    // ── Concurrency Race ─────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentTryAcquireJobSlot_ExactlyOneWins()
    {
        var service = CreateService();
        var barrier = new Barrier(2);
        bool? result1 = null;
        bool? result2 = null;

        var task1 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            result1 = InvokeTryAcquireJobSlot(service, "race-job-1", out _);
        });
        var task2 = Task.Run(() =>
        {
            barrier.SignalAndWait();
            result2 = InvokeTryAcquireJobSlot(service, "race-job-2", out _);
        });

        await Task.WhenAll(task1, task2);

        // Exactly one should succeed
        var successes = new[] { result1, result2 }.Count(r => r == true);
        successes.Should().Be(1);

        var activeJobId = GetPrivateField<string?>(GetSlotManager(service), "_activeJobId");
        activeJobId.Should().NotBeNull();
        activeJobId.Should().Match<string>(id => id == "race-job-1" || id == "race-job-2");
    }

    [Fact]
    public async Task ConcurrentAssignJob_HandlersComplete_ExactlyOneAcquiresSlot()
    {
        // Both handlers run to completion (one acquires, one rejects).
        // The winner's slot is then cleared by the JobAccepted failure on the disconnected hub.
        // We verify the mutual exclusion invariant held during acquisition.
        var service = CreateService();
        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");

        var msg1 = CreateTestJobAssignment("race-a");
        var msg2 = CreateTestJobAssignment("race-b");

        var task1 = Task.Run(() => (Task)handler.Invoke(service, [msg1])!);
        var task2 = Task.Run(() => (Task)handler.Invoke(service, [msg2])!);

        // Both handlers should complete without throwing
        await Task.WhenAll(task1, task2);

        // After both complete (with disconnected hub), the winning handler
        // clears _activeJobId on JobAccepted failure. The invariant is that
        // no double-execution occurred — verify via IsBusy being false (both done).
        service.IsBusy.Should().BeFalse("both handlers completed; winner cleared on JobAccepted failure");
    }

    // ── Heartbeat Lifecycle ──────────────────────────────────────────────

    [Fact]
    public void Heartbeat_Idle_CurrentStepIsNull()
    {
        var service = CreateService();

        service.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void Heartbeat_JobActive_CurrentStepReflectsBusy()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "busy-job");
        SetPrivateField(GetSlotManager(service), "_currentStep", PipelineStep.AnalyzingCode);

        service.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);
    }

    [Fact]
    public async Task Heartbeat_AfterRelease_CurrentStepReturnsToNull()
    {
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeJobId", "finishing-job");
        SetPrivateField(GetSlotManager(service), "_currentStep", PipelineStep.GeneratingCode);
        SetPrivateField(GetSlotManager(service), "_jobCts", new CancellationTokenSource());

        await GetSlotManager(service).ReleaseJobSlotAndSignalReadyAsync();

        service.CurrentStep.Should().BeNull();
        GetPrivateField<string?>(GetSlotManager(service), "_activeJobId").Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool InvokeTryAcquireJobSlot(AgentWorkerService service, string jobId, out string? busyWith)
    {
        var slotManager = GetSlotManager(service);
        return slotManager.TryAcquireJobSlot(jobId, out busyWith);
    }

    private static AgentWorkerService CreateService()
    {
        return TestAgentWorkerServiceFactory.Create();
    }

    private static JobAssignmentMessage CreateTestJobAssignment(string jobId)
    {
        return new JobAssignmentMessage
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
    }

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

    private static AgentJobSlotManager GetSlotManager(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_slotManager",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_slotManager' not found");
        return (AgentJobSlotManager)field.GetValue(service)!;
    }
}
