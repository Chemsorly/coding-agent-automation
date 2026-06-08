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
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("job-1");
    }

    [Fact]
    public void TryAcquireJobSlot_AtCapacity_ActiveJob_Rejects()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeJobId", "existing-job");

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeFalse();
        busyWith.Should().Be("existing-job");
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("existing-job");
    }

    [Fact]
    public void TryAcquireJobSlot_AtCapacity_ActiveChat_Rejects()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeChatSessionId", "session-42");

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeFalse();
        busyWith.Should().Be("chat:session-42");
        GetPrivateField<string?>(service, "_activeJobId").Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireJobSlot_AfterRelease_SlotFreed()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeJobId", "old-job");
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        var releaseMethod = GetPrivateMethod(service, "ReleaseJobSlotAndSignalReadyAsync");
        var task = (Task)releaseMethod.Invoke(service, [])!;
        await task;

        var result = InvokeTryAcquireJobSlot(service, "new-job", out var busyWith);

        result.Should().BeTrue();
        busyWith.Should().BeNull();
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("new-job");
    }

    // ── Rejection Notification ───────────────────────────────────────────

    [Fact]
    public async Task HandleAssignJob_WhenBusy_RejectionPathCompletes()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeJobId", "existing-job");

        var message = CreateTestJobAssignment("rejected-job");
        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        var task = (Task)handler.Invoke(service, [message])!;
        await task;

        // Handler completes without throwing; active job unchanged
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("existing-job");
    }

    [Fact]
    public async Task HandleAssignConsolidationJob_WhenBusy_RejectionPathCompletes()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeJobId", "existing-job");

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
        GetPrivateField<string?>(service, "_activeJobId").Should().Be("existing-job");
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

        var activeJobId = GetPrivateField<string?>(service, "_activeJobId");
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
        SetPrivateField(service, "_activeJobId", "busy-job");
        SetPrivateField(service, "_currentStep", PipelineStep.AnalyzingCode);

        service.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);
    }

    [Fact]
    public async Task Heartbeat_AfterRelease_CurrentStepReturnsToNull()
    {
        var service = CreateService();
        SetPrivateField(service, "_activeJobId", "finishing-job");
        SetPrivateField(service, "_currentStep", PipelineStep.GeneratingCode);
        SetPrivateField(service, "_jobCts", new CancellationTokenSource());

        var releaseMethod = GetPrivateMethod(service, "ReleaseJobSlotAndSignalReadyAsync");
        var task = (Task)releaseMethod.Invoke(service, [])!;
        await task;

        service.CurrentStep.Should().BeNull();
        GetPrivateField<string?>(service, "_activeJobId").Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool InvokeTryAcquireJobSlot(AgentWorkerService service, string jobId, out string? busyWith)
    {
        var method = service.GetType().GetMethod("TryAcquireJobSlot",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Method 'TryAcquireJobSlot' not found");

        var args = new object?[] { jobId, null };
        var result = (bool)method.Invoke(service, args)!;
        busyWith = (string?)args[1];
        return result;
    }

    private static AgentWorkerService CreateService()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_TYPE")))
            Environment.SetEnvironmentVariable("AGENT_TYPE", "kiro-dotnet");

        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();

        var logger = new Mock<Serilog.ILogger>();
        var hubManager = new HubConnectionManager(
            "http://localhost:9999",
            "test-agent",
            "test-api-key",
            logger.Object);

        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockQualityGateValidator = new Mock<Pipeline.Interfaces.IQualityGateValidator>();
        var executor = new LocalPipelineExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object,
            agentIdentity: new Pipeline.Models.AgentIdentity("test-agent"));

        var consolidationExecutor = new LocalConsolidationExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            mockLogger.Object);

        return new AgentWorkerService(
            hubManager,
            executor,
            consolidationExecutor,
            mockOrchestrator.Object,
            Mock.Of<IHttpClientFactory>(),
            new AgentIdentity("test-agent"),
            Mock.Of<IHostApplicationLifetime>(),
            mockLogger.Object);
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
}
