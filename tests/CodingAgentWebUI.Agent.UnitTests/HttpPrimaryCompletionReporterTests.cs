using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

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

    // ── Serialize result ─────────────────────────────────────────────────

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
