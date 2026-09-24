using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="HttpPrimaryCompletionReporter"/>.
/// Covers constructor validation, HTTP primary channel dispatch,
/// SignalR secondary channel invocation and failure tolerance.
/// </summary>
public class HttpPrimaryCompletionReporterTests
{
    private readonly Mock<IWorkItemLifecycleClient> _lifecycleClient = new();
    private readonly Mock<IAgentConnectionManager> _connectionManager = new();
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly AgentId _agentId = new("test-agent");
    private const string WorkItemId = "wi-001";

    private HttpPrimaryCompletionReporter CreateSut(
        string? workItemId = null,
        IWorkItemLifecycleClient? client = null,
        IAgentConnectionManager? manager = null,
        AgentId agentId = default,
        Serilog.ILogger? logger = null)
        => new(
            workItemId ?? WorkItemId,
            client ?? _lifecycleClient.Object,
            manager ?? _connectionManager.Object,
            agentId.Value is null ? _agentId : agentId,
            logger ?? _logger.Object);

    // ── Constructor guards ────────────────────────────────────────────────

    // TODO: [WARNING] InlineData(3) for AgentId (index 3) is intentionally omitted because AgentId is a
    // struct — ThrowIfNull on a struct is a no-op. If a null-guard for agentId.Value was later added
    // to the constructor (for consistency with the default(AgentId) gap), index 3 should be re-added
    // here using a different approach (e.g., passing default(AgentId) and asserting ArgumentException).
    // See: review-findings.md [WARNING] HttpPrimaryCompletionReporterTests.cs:57
    [Theory]
    [InlineData(0)] // workItemId
    [InlineData(1)] // lifecycleClient
    [InlineData(2)] // connectionManager
    [InlineData(4)] // logger
    public void Constructor_NullArgument_ThrowsArgumentNullException(int nullIndex)
    {
        object?[] args = [WorkItemId, _lifecycleClient.Object, _connectionManager.Object, _agentId, _logger.Object];
        args[nullIndex] = null;

        var act = () => new HttpPrimaryCompletionReporter(
            (string)args[0]!,
            (IWorkItemLifecycleClient)args[1]!,
            (IAgentConnectionManager)args[2]!,
            (AgentId)args[3]!,
            (Serilog.ILogger)args[4]!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportCompletionAsync_NullPayload_ThrowsArgumentNullException()
    {
        var sut = CreateSut();
        var act = async () => await sut.ReportCompletionAsync("job-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── HTTP primary channel ─────────────────────────────────────────────

    [Fact]
    public async Task ReportCompletionAsync_CompletedStep_PostsSucceededStatus()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_CancelledStep_PostsCancelledStatus()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Cancelled, CompletedAt = DateTimeOffset.UtcNow };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u => u.Status == "Cancelled"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_FailedStep_PostsFailedStatus()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "Build error",
            FailureCategory = Pipeline.Models.FailureReason.AgentError,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Failed" &&
                u.ErrorMessage == "Build error" &&
                u.FailureReason == nameof(Pipeline.Models.FailureReason.AgentError)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_FailedWithNoCategory_UsesAgentErrorDefault()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureCategory = null,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.FailureReason == nameof(Pipeline.Models.FailureReason.AgentError)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_SucceededStep_FailureReasonIsNull()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u => u.FailureReason == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_SetsAgentIdOnUpdate()
    {
        var agentId = new AgentId("my-agent");
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(agentId: agentId);
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u => u.AgentId == "my-agent"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── SignalR secondary channel ─────────────────────────────────────────

    [Fact]
    public async Task ReportCompletionAsync_AfterHttpSuccess_InvokesSignalR()
    {
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _connectionManager.Verify(m => m.InvokeAsync(
            It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_SignalRFails_DoesNotThrow_LogsWarning()
    {
        // SignalR is secondary/non-fatal: its failure should be swallowed and logged
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Hub not connected"));

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // Must not throw — SignalR failure is non-fatal
        var act = async () => await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Warning must be logged for observability
        // TODO: [WARNING] This Verify matches only the two-argument Warning(Exception, string) overload.
        // If the implementation is changed to use Warning(Exception, string, object) (e.g., to log jobId),
        // this assertion would silently pass while matching zero calls on the new overload. Tighten to
        // match the exact overload and message template used in the implementation.
        // See: review-findings.md [WARNING] HttpPrimaryCompletionReporterTests.cs:220
        _logger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_HttpFails_Throws()
    {
        // HTTP is primary: its failure should propagate
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var act = async () => await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ReportCompletionAsync_PostStatusReturnsFalse_LogsWarning()
    {
        // Arrange: simulate server rejecting the status transition (400 or 404)
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // Act: must not throw — rejection is observable via log, not exception
        var act = async () => await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert: Warning is logged with WorkItemId and terminalStatus as structured properties.
        // Uses the generic Warning<T0,T1>(string, T0, T1) overload — NOT Warning(string, object[]).
        // C# overload resolution selects the generic form when two explicit typed args are passed.
        // TODO: Tighten these assertions to pin the actual values rather than accepting any string:
        //   It.Is<string>(s => s == WorkItemId) for T0 and It.Is<string>(s => s == "Succeeded") for T1.
        //   As written, the test does not catch argument transposition (wrong order of WorkItemId vs status).
        // TODO: If the Serilog ILogger overload used in production ever changes (e.g., a third structured
        //   property is added), this Verify may silently match zero invocations. Add a complementary
        //   assertion on the message template string to make the test robust against overload shifts.
        // TODO: Add parameterized cases for PipelineStep.Failed and PipelineStep.Cancelled to cover all
        //   three terminalStatus values — the current test only exercises the Completed/"Succeeded" path.
        _logger.Verify(l => l.Warning(
            It.Is<string>(s => s.Contains("transition was rejected")),
            It.IsAny<string>(),   // WorkItemId (T0 = string)
            It.IsAny<string>()),  // terminalStatus (T1 = string)
            Times.Once);
    }

    // ── BranchName intermediate Running POST (issue #2687) ───────────────────

    /// <summary>
    /// When the payload carries a BranchName, ReportCompletionAsync must fire an intermediate
    /// Running+BranchName POST before the terminal status POST, so WorkItems.BranchName is
    /// populated in Postgres before the item transitions out of the active state.
    /// </summary>
    [Fact]
    public async Task ReportCompletionAsync_WithBranchName_PostsRunningBeforeTerminal()
    {
        // Arrange: capture all PostStatusAsync calls in order
        var calls = new List<WorkItemStatusUpdate>();
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkItemStatusUpdate, CancellationToken>((_, u, _) => calls.Add(u))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            BranchName = "feature/my-branch"
        };

        // Act
        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        // Assert: exactly two PostStatusAsync calls — Running first, then Succeeded
        calls.Should().HaveCount(2, "an intermediate Running+BranchName POST and a terminal Succeeded POST are required");
        calls[0].Status.Should().Be("Running", "the intermediate POST must use Running status");
        calls[0].BranchName.Should().Be("feature/my-branch", "the intermediate POST must carry the branch name");
        calls[0].AgentId.Should().Be("test-agent", "AgentId must be set on the intermediate POST");
        calls[1].Status.Should().Be("Succeeded", "the terminal POST must come after the intermediate POST");
    }

    /// <summary>
    /// When the payload has no BranchName (null), no intermediate Running POST is fired.
    /// Only the terminal status POST is sent — this is the existing behavior and must not change.
    /// </summary>
    [Fact]
    public async Task ReportCompletionAsync_NoBranchName_PostsOnlyTerminalStatus()
    {
        // Arrange
        var calls = new List<WorkItemStatusUpdate>();
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkItemStatusUpdate, CancellationToken>((_, u, _) => calls.Add(u))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            BranchName = null     // no branch — no intermediate POST
        };

        // Act
        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        // Assert: exactly one call — the terminal Succeeded POST only
        calls.Should().ContainSingle("no intermediate POST when BranchName is null");
        calls[0].Status.Should().Be("Succeeded");
        calls[0].BranchName.Should().BeNull();
    }

    /// <summary>
    /// When the intermediate Running+BranchName POST is rejected (returns false), the reporter
    /// must log a warning but continue and still send the terminal status POST.
    /// The terminal transition is what matters for correctness; the BranchName population is best-effort.
    /// </summary>
    [Fact]
    public async Task ReportCompletionAsync_IntermediateRunningPostRejected_LogsWarningAndContinues()
    {
        // Arrange: intermediate (Running) POST returns false; terminal POST returns true
        var callCount = 0;
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++callCount > 1); // first call returns false, subsequent calls return true
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            BranchName = "feature/race-branch"
        };

        // Act — must not throw
        var act = async () => await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);
        await act.Should().NotThrowAsync("a rejected intermediate POST must not abort the completion sequence");

        // Assert: terminal POST was still sent
        _lifecycleClient.Verify(
            c => c.PostStatusAsync(WorkItemId, It.Is<WorkItemStatusUpdate>(u => u.Status == "Succeeded"), It.IsAny<CancellationToken>()),
            Times.Once,
            "the terminal Succeeded POST must still be sent even when the intermediate POST was rejected");

        // Assert: warning was logged for the rejection
        _logger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("BranchName") && s.Contains("Running POST rejected")),
                It.IsAny<string>()),
            Times.Once,
            "a warning must be logged when the intermediate Running POST is rejected");
    }

    // ── Serialize result ─────────────────────────────────────────────────

    [Fact]
    public async Task ReportCompletionAsync_ConflictRestartStep_PostsSucceededStatus_WithNoFailureReason()
    {
        // Issue #2956: ConflictRestart must be persisted as Succeeded (not Failed/AgentError)
        // on the HTTP primary path, matching CompletionOutcomeResolver's existing hub-path behavior.
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.ConflictRestart,
            FinalLabel = "agent:next", // re-queue label is unchanged
            CompletedAt = DateTimeOffset.UtcNow
        };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Succeeded" &&
                u.FailureReason == null),
            // TODO: [WARNING] ErrorMessage is also cleared for non-Failed results by the production
            // change (ErrorMessage = terminalWorkItemStatus == Failed ? payload.FailureReason : null).
            // This assertion does not verify u.ErrorMessage == null. A regression that accidentally
            // re-enables ErrorMessage for ConflictRestart would not be caught here.
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_WontDoGate_PostsSucceededStatus_WithGateRejectedFailureReason()
    {
        // Issue #2956: won't-do gate outcomes (PipelineStep.Completed + FailureCategory=GateRejected)
        // must be persisted with FailureReason = "GateRejected" in the DB, even though the
        // WorkItemStatus is Succeeded. This verifies the FailureCategory fallback in
        // HttpPrimaryCompletionReporter: persistedFailureReason = terminalFailureReason?.ToString()
        //   ?? payload.FailureCategory?.ToString()
        // (terminalFailureReason is null for Succeeded; FailureCategory carries GateRejected).
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,           // won't-do uses Completed step
            FinalLabel = AgentLabels.WontDo,
            FailureCategory = Pipeline.Models.FailureReason.GateRejected,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        _lifecycleClient.Verify(c => c.PostStatusAsync(
            WorkItemId,
            It.Is<WorkItemStatusUpdate>(u =>
                u.Status == "Succeeded" &&
                u.FailureReason == nameof(Pipeline.Models.FailureReason.GateRejected)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportCompletionAsync_PayloadWithAllFields_SerializesResult()
    {
        WorkItemStatusUpdate? captured = null;
        _lifecycleClient
            .Setup(c => c.PostStatusAsync(WorkItemId, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorkItemStatusUpdate, CancellationToken>((_, update, _) => captured = update)
            .ReturnsAsync(true);
        _connectionManager
            .Setup(m => m.InvokeAsync(It.IsAny<Func<Microsoft.AspNetCore.SignalR.Client.HubConnection, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await sut.ReportCompletionAsync("job-1", payload, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Result.Should().NotBeNullOrEmpty("serialized payload should be set");
    }
}
