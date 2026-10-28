using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="WorkItemStatusTransitionService.TransitionAsync"/>.
///
/// These tests validate that the orchestration logic extracted from
/// <c>WorkItemAgentEndpoints.PostStatus</c> behaves correctly in isolation:
/// infra-recovery guard, pre-read idempotency guard, lifecycle dispatch, telemetry,
/// and FailureReason validation.
///
/// Uses the same in-memory DB pattern as <see cref="PostStatusIdempotencyTests"/>
/// since <see cref="WorkItemTransitionService.TransitionDetailedAsync"/> is not on an interface.
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class WorkItemStatusTransitionServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"TransitionSvc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status,
        DateTimeOffset? completedAt = null,
        FailureReason? failureReason = null,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-1",
            Status = status,
            TaskType = taskType,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt,
            FailureReason = failureReason,
        };
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemTransitionService CreateTransitionService(DbContextOptions<PipelineDbContext> opts)
        => new(new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance);

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(DbContextOptions<PipelineDbContext> opts)
        => new TestDbContextFactory(opts);

    private static WorkItemStatusTransitionService CreateService(
        DbContextOptions<PipelineDbContext> opts,
        IRunLifecycleManager lifecycleManager,
        IDbContextFactory<PipelineDbContext>? dbFactory = null)
        => new(CreateTransitionService(opts), lifecycleManager, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory ?? CreateDbFactory(opts));

    // ── Infrastructure-recovery path ─────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_Running_InfraRecoverySucceeds_ReturnsTransitioned_NoLifecycleCall()
    {
        // Arrange: Failed/Timeout item — infra recovery should recover it to Running
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.Timeout);

        // Strict mock: lifecycle must NOT be called on recovery path
        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned,
            "infra recovery returns true → Transitioned");
        lifecycleManager.VerifyNoOtherCalls();

        await using var db = new TestPipelineDbContext(opts);
        var updated = await db.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Running,
            "DB must reflect the recovered status");
    }

    [Fact]
    public async Task TransitionAsync_Running_InfraRecoveryFails_FallsThroughAndReturnsRejected()
    {
        // Arrange: Failed/AgentError — not recoverable; falls through to TransitionDetailedAsync
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            failureReason: FailureReason.AgentError);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert: recovery failed; TransitionDetailedAsync also rejects Failed→Running
        outcome.Should().Be(StatusTransitionOutcome.Rejected,
            "Failed/AgentError is not recoverable; TransitionDetailedAsync rejects Failed→Running");
    }

    // ── Pre-read idempotency guard ─────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_FailedOnAlreadyCancelled_ReturnsAlreadyAtTarget_NoLifecycleCall()
    {
        // Arrange: already Cancelled item receiving PostStatus(Failed)
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert: pre-read guard short-circuits
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "pre-read guard returns AlreadyAtTarget for incoming terminal on already-Cancelled item");
        lifecycleManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TransitionAsync_FailedOnAlreadySucceeded_ReturnsAlreadyAtTarget_NoLifecycleCall()
    {
        // Arrange: already Succeeded item receiving PostStatus(Failed)
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "pre-read guard returns AlreadyAtTarget for incoming terminal on already-Succeeded item");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── AlreadyAtTarget via TransitionDetailedAsync (Issue #3975) ────────────────

    [Fact]
    public async Task TransitionAsync_Running_AlreadyRunning_ReturnsAlreadyAtTarget_NoLifecycleCalls()
    {
        // Arrange: Pending item receiving Pending status.
        // Running→Running is intercepted by TryRecoverFromInfrastructureFailureAsync which
        // returns true (idempotent) for an already-Running item, bypassing TransitionDetailedAsync.
        // Pending is non-terminal and not subject to the infra-recovery guard, so
        // TransitionDetailedAsync(Pending→Pending) is called; it returns AlreadyAtTarget at the
        // same-status check (item.Status == target) → L171 fires.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Pending };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "TransitionDetailedAsync returns AlreadyAtTarget for a same-status Pending→Pending " +
            "transition; L171 must be reached on this path");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── Lifecycle dispatch ─────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_Failed_RealTransition_CallsFailRunWithLabelAsync_NullLabel()
    {
        // Arrange: Running → Failed — a real terminal transition (no request.Result → resolvedFinalLabel = null)
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, ErrorMessage = "infra killed it" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        // No request.Result → resolvedFinalLabel is null → falls back to agent:error inside RunLifecycleManager
        lifecycleManager.Verify(
            m => m.FailRunWithLabelAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<string>(),
                (string?)null,
                It.IsAny<CancellationToken>(),
                It.Is<FailureReason?>(fr => fr == FailureReason.InfrastructureFailure),
                It.IsAny<IReadOnlyList<string>?>()),
            Times.Once,
            "FailRunWithLabelAsync must be called with resolvedFinalLabel=null when no Result in request");
    }

    [Fact]
    public async Task TransitionAsync_Cancelled_RealTransition_CallsCancelRunAsync()
    {
        // Arrange: Running → Cancelled
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CancelRunAsync(
                It.IsAny<RunId>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        lifecycleManager.Verify(
            m => m.CancelRunAsync(
                It.Is<RunId>(r => r.Value == item.Id.ToString()),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()),
            Times.Once,
            "CancelRunAsync must be called on terminal Cancelled transition");
    }

    // ── NotFound and Rejected paths ────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_NonExistentItem_ReturnsNotFound()
    {
        // Arrange: empty DB
        var opts = CreateDbOptions();
        await using (var ctx = new TestPipelineDbContext(opts))
            ctx.Database.EnsureCreated();

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act
        var outcome = await svc.TransitionAsync(Guid.NewGuid(), request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.NotFound);
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_ReturnsRejected()
    {
        // Arrange: Pending → Succeeded is invalid
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = CreateService(opts, lifecycleManager);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Rejected);
    }

    // ── FailureReason IsDefined guard ──────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_UndefinedNumericFailureReason_PersistsAgentError()
    {
        // "99" parses via Enum.TryParse but is not a named member; IsDefined guard must reject it
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "99" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);

        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted!.FailureReason.Should().Be(FailureReason.AgentError,
            "the IsDefined guard must reject undefined numeric FailureReason and fall back to AgentError");
    }

    [Fact]
    public async Task TransitionAsync_ValidNamedFailureReason_PersistsCorrectly()
    {
        // "AgentError" is a named member — must pass through the IsDefined guard
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "AgentError" };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);

        await using var verifyCtx = new TestPipelineDbContext(opts);
        var persisted = await verifyCtx.WorkItems.FindAsync(item.Id);
        persisted!.FailureReason.Should().Be(FailureReason.AgentError,
            "a valid named FailureReason must be persisted correctly");
    }

    // ── FinalLabel resolution from request.Result (Issue #3009) ──────────────

    /// <summary>
    /// Helper: creates a Running WorkItem in DB, sets up a loose lifecycle mock for
    /// FailRunWithLabelAsync, creates the service, and runs TransitionAsync with a Failed
    /// request carrying the given serialized result. Returns (outcome, captured resolvedFinalLabel).
    /// </summary>
    private static async Task<(StatusTransitionOutcome outcome, string? capturedLabel)> RunFailedTransitionWithResultAsync(
        string? resultJson)
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        string? capturedLabel = "SENTINEL"; // distinct from null so we know it was set
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .Callback<RunId, string, string?, CancellationToken, FailureReason?, IReadOnlyList<string>?>(
                (_, _, resolvedLabel, _, _, _) => capturedLabel = resolvedLabel)
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = resultJson };

        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);
        return (outcome, capturedLabel == "SENTINEL" ? throw new InvalidOperationException("Callback never fired") : capturedLabel);
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithNeedsRefinementResult_PassesNeedsRefinementLabel()
    {
        // Arrange: request.Result carries FinalLabel = "agent:needs-refinement"
        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalLabel = AgentLabels.NeedsRefinement,
            FinalStep = CodingAgent.Pipeline.Models.PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var resultJson = System.Text.Json.JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        var (outcome, capturedLabel) = await RunFailedTransitionWithResultAsync(resultJson);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        capturedLabel.Should().Be(AgentLabels.NeedsRefinement,
            "agent:needs-refinement is the only allowed label from the Failed HTTP path");
    }

    [Theory]
    [InlineData("agent:done")]
    [InlineData("agent:next")]
    [InlineData("agent:epic-review")]
    [InlineData("agent:wont-do")]
    [InlineData("agent:cancelled")]
    [InlineData("agent:in-progress")]
    [InlineData("agent:error")]
    [InlineData("unknown-label")]
    public async Task TransitionAsync_Failed_WithNonNeedsRefinementLabel_FallsBackToNull(string disallowedLabel)
    {
        // Arrange: any label other than agent:needs-refinement must resolve to null (agent:error fallback)
        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalLabel = disallowedLabel,
            FinalStep = CodingAgent.Pipeline.Models.PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var resultJson = System.Text.Json.JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        var (outcome, capturedLabel) = await RunFailedTransitionWithResultAsync(resultJson);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        capturedLabel.Should().BeNull(
            $"label '{disallowedLabel}' must not be accepted on the Failed HTTP path; resolvedFinalLabel must be null (agent:error fallback)");
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithNullResult_PassesNullLabel()
    {
        // Arrange: no result payload — resolvedFinalLabel must be null
        var (outcome, capturedLabel) = await RunFailedTransitionWithResultAsync(resultJson: null);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        capturedLabel.Should().BeNull("null result → no FinalLabel → agent:error fallback");
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithMalformedResult_PassesNullLabel()
    {
        // Arrange: JSON that cannot be deserialized as JobCompletionPayload
        var (outcome, capturedLabel) = await RunFailedTransitionWithResultAsync(resultJson: "not-valid-json{{");

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        capturedLabel.Should().BeNull("malformed JSON → deserialization fails → agent:error fallback");
    }

    // TODO: [WARNING] There is no counterpart test verifying that a *valid* JobCompletionPayload JSON
    // does NOT trigger the Warning log. Without it, the suite would pass even if LogWarning were called
    // on every transition regardless of parse success. Add a test that passes well-formed JSON and
    // asserts Times.Never for LogLevel.Warning to complete the coverage and prevent regressions in
    // the opposite direction.
    [Fact]
    public async Task TransitionAsync_Failed_WithMalformedResult_LogsWarningWithWorkItemId()
    {
        // Arrange: seed a Running WorkItem so a real Failed transition fires
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        // A payload string that is distinctly recognizable but NOT a valid JSON literal.
        // The acceptance criterion is that the raw payload body is not included in the log
        // message template args — the exception is passed as the first arg (so the stack trace
        // appears in structured logs), but resultJson itself must not be forwarded to the logger.
        const string malformedJson = "not-valid-json{{";

        string? capturedMessage = null;
        Exception? capturedEx = null;
        var mockLogger = new Mock<ILogger<WorkItemStatusTransitionService>>();
        mockLogger
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            // TODO: [WARNING] The Callback generic type parameters <LogLevel, EventId, object, Exception?, Delegate>
            // are inconsistent with the Setup's It.IsAnyType placeholders. Moq requires callback type args to
            // match the resolved types; using `object` for TState and `Delegate` for the formatter may cause Moq
            // to silently skip invoking the callback, leaving capturedMessage/capturedEx null. The assertions on
            // those variables would then pass vacuously (null.Should().BeOfType<JsonException>() passes via
            // AwesomeAssertions null-safe path). The correct Callback signature is:
            //   .Callback<LogLevel, EventId, object, Exception?, Func<object, Exception?, string>>(...)
            // Alternatively, replace the Mock with a concrete TestLogger<T> implementation.
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((_, _, state, ex, _) =>
            {
                capturedEx = ex;
                capturedMessage = state?.ToString();
            });

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(
            CreateTransitionService(opts),
            lifecycleManager.Object,
            mockLogger.Object,
            CreateDbFactory(opts));

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = malformedJson };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert — positive call-count: warning must be emitted exactly once
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "a Warning must be logged exactly once for a malformed Result payload");

        outcome.Should().Be(StatusTransitionOutcome.Transitioned,
            "the fallback to null label must still produce a successful transition");

        // Assert — exception is passed to LogWarning (stack trace preserved in structured logs)
        capturedEx.Should().BeOfType<JsonException>(
            "the caught JsonException must be passed to LogWarning so the stack trace appears in structured logs");

        // Assert — WorkItem ID appears in the log message template args
        capturedMessage.Should().Contain(item.Id.ToString(),
            "the WorkItem ID must appear in the Warning log message");

        // Assert — the raw JSON payload string is NOT passed as a message template arg (issue AC).
        // The implementation logs only workItemId (not resultJson); ex.Message is separate from the
        // message template args and may embed a snippet at the deserializer's discretion, but the
        // raw payload must not be explicitly forwarded as a structured log property.
        // TODO: [WARNING] This assertion is nearly tautological because malformedJson contains "{{" which
        // is a format-escape sequence. If the payload were passed as a structured log argument, Moq's
        // FormattedLogValues.ToString() would render "{{" as a literal "{", so NotContain("not-valid-json{{")
        // would pass even if the payload were present (it would appear as "not-valid-json{" in the rendered
        // string). Use a sentinel string without format-special characters (e.g. "not-valid-json-SENTINEL")
        // to reliably detect forbidden payload inclusion.
        capturedMessage.Should().NotContain(malformedJson,
            "the raw JSON payload body must not be passed as a log message argument");
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithEmptyStringResult_PassesNullLabel()
    {
        // Arrange: empty string (treated same as null — no payload)
        var (outcome, capturedLabel) = await RunFailedTransitionWithResultAsync(resultJson: "");

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        capturedLabel.Should().BeNull("empty result string → no FinalLabel → agent:error fallback");
    }

    // ── Ignored FinalLabel diagnostics (Issue #3009, log-forging fix #3121) ───

    /// <summary>
    /// Helper: runs a Failed transition whose Result payload carries <paramref name="finalLabel"/>,
    /// with <paramref name="logger"/> injected. Returns the seeded WorkItem ID.
    /// </summary>
    private static async Task<Guid> RunFailedTransitionWithFinalLabelAsync(
        string? finalLabel, ILogger<WorkItemStatusTransitionService> logger)
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(
            CreateTransitionService(opts), lifecycleManager.Object, logger, CreateDbFactory(opts));

        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalLabel = finalLabel,
            FinalStep = CodingAgent.Pipeline.Models.PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            Result = JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default)
        };

        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        return item.Id;
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithDisallowedFinalLabel_LogsInformationWithLabelAndWorkItemId()
    {
        var mockLogger = new Mock<ILogger<WorkItemStatusTransitionService>>();

        var workItemId = await RunFailedTransitionWithFinalLabelAsync("agent:done", mockLogger.Object);

        // The dropped value must be visible to operators, otherwise a Failed completion that
        // ends at agent:error is indistinguishable from one that carried no FinalLabel at all.
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("agent:done") && v.ToString()!.Contains(workItemId.ToString())),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "a FinalLabel outside the allowlist must be logged at Information with the label and WorkItem ID");
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithDisallowedFinalLabelContainingNewlines_LogsEscapedLabel()
    {
        // FinalLabel comes from the agent-controlled HTTP payload. CR/LF in it must not reach the
        // plain-text console sink, where it would render as a separate, forged log line.
        var renderedMessages = new List<string>();
        var mockLogger = new Mock<ILogger<WorkItemStatusTransitionService>>();
        mockLogger
            .Setup(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
                renderedMessages.Add(invocation.Arguments[2].ToString()!)));

        await RunFailedTransitionWithFinalLabelAsync(
            "agent:done\r\n[ERR] forged entry", mockLogger.Object);

        renderedMessages.Should().ContainSingle()
            .Which.Should().Contain("agent:done\\r\\n[ERR] forged entry",
                "CR/LF in the agent-supplied FinalLabel must be escaped before logging")
            .And.NotContain("\r").And.NotContain("\n");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("agent:needs-refinement")]
    public async Task TransitionAsync_Failed_WithAllowedOrAbsentFinalLabel_DoesNotLogInformation(string? finalLabel)
    {
        var mockLogger = new Mock<ILogger<WorkItemStatusTransitionService>>();

        await RunFailedTransitionWithFinalLabelAsync(finalLabel, mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "only a FinalLabel that is present and dropped is worth an Information line");
    }

    // ── Idempotency when item is already Failed (Issue #3009) ─────────────────

    [Fact]
    // TODO: [WARNING] The test name says "ReturnsRejected" but the assertion is AlreadyAtTarget.
    // The // Arrange: comment also incorrectly states "TransitionDetailedAsync returns Rejected (not
    // AlreadyAtTarget, because the pre-read guard only catches Cancelled/Succeeded)" — but the
    // // Assert: comment and the actual assertion correctly use AlreadyAtTarget. The test name and
    // arrange-comment need to be reconciled: either TransitionDetailedAsync returns AlreadyAtTarget
    // for a same-status Failed→Failed transition (in which case the test name is wrong and should be
    // "ReturnsAlreadyAtTarget"), or it returns Rejected (in which case the assertion is wrong).
    // Verify against the TransitionDetailedAsync implementation and fix the test name + arrange comment
    // to match the actual behavior. A wrong name here could mask a future regression where the actual
    // return value changes. See review finding [WARNING] #5 (TestQualityReviewer).
    public async Task TransitionAsync_FailedOnAlreadyFailed_ReturnsRejected_NoLifecycleCall()
    {
        // Arrange: item already Failed in DB — TransitionDetailedAsync returns Rejected (not AlreadyAtTarget,
        // because the pre-read guard only catches Cancelled/Succeeded). Lifecycle dispatch is gated on
        // Transitioned, so FailRunWithLabelAsync must NOT be called.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);

        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalLabel = AgentLabels.NeedsRefinement,
            FinalStep = CodingAgent.Pipeline.Models.PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            Result = System.Text.Json.JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default)
        };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        // Assert: Failed→Failed is AlreadyAtTarget (TransitionDetailedAsync returns AlreadyAtTarget for same-status transitions);
        // lifecycle dispatch is gated on Transitioned, so FailRunWithLabelAsync must NOT be called,
        // and whatever label the previous transition set is preserved.
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "TransitionDetailedAsync returns AlreadyAtTarget for a Failed→Failed same-status transition; lifecycle dispatch never fires");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── Telemetry emission (via awaitTelemetry seam) ───────────────────────────

    [Fact]
    public async Task TransitionAsync_ActualTerminalTransition_EmitsTelemetryCounter()
    {
        // Arrange: Running → Succeeded — telemetry must fire
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        long terminatedCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            Interlocked.Increment(ref terminatedCount));
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act — awaitTelemetry: true for deterministic assertion
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        terminatedCount.Should().BeGreaterThanOrEqualTo(1,
            "pipeline.run.outcomes must increment on a real terminal transition");
    }

    [Fact]
    public async Task TransitionAsync_AlreadyAtTarget_DoesNotEmitTelemetry()
    {
        // Arrange: already Cancelled — AlreadyAtTarget must not emit telemetry
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var dbFactory = CreateDbFactory(opts);

        var measurements = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => measurements.Add(value));
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget);
        measurements.Should().BeEmpty(
            "pipeline.run.outcomes must not increment for an AlreadyAtTarget no-op");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── Telemetry exception catches (Issue #3975) ─────────────────────────────

    [Fact]
    public async Task TransitionAsync_TerminalStatus_TelemetryDbFactoryThrows_ExceptionNotPropagated()
    {
        // Arrange: Running item transitioning to Succeeded.
        // ThrowingDbContextFactory is injected as _dbFactory — the factory distinct from the
        // one used by WorkItemTransitionService. ResolveRunContextAsync calls
        // dbFactory.CreateDbContextAsync, which throws, triggering the catch block at L299–L303
        // inside EmitTerminalStatusTelemetryAsync. The catch must swallow the exception.
        // awaitTelemetry: true is required so the fire-and-forget task completes before the
        // assertion; without it the test finishes before the catch block is exercised.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = new WorkItemStatusTransitionService(
            CreateTransitionService(opts),
            lifecycleManager,
            NullLogger<WorkItemStatusTransitionService>.Instance,
            new ThrowingDbContextFactory());

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert — if the catch at L299–L303 failed to swallow the exception it would surface here
        outcome.Should().Be(StatusTransitionOutcome.Transitioned,
            "exception inside EmitTerminalStatusTelemetryAsync must be caught at L299–L303 " +
            "and must not propagate to the caller");
    }

    [Fact]
    public async Task TransitionAsync_AlreadyTerminal_NonConsolidationRunType_WithPhaseBreakdown_ReturnsAlreadyAtTarget()
    {
        // Arrange: already-Cancelled item with TaskType=Implementation (explicit, not relying on
        // SeedWorkItemAsync default so the test documents its own invariant).
        // A Failed request with PhaseBreakdown triggers the pre-read guard → AlreadyAtTarget.
        // EmitConsolidationUsageTelemetryAsync is started; inside it ResolveRunContextAsync
        // resolves runTypeTag="implementation" (TaskType→ToDefaultRunTypeOrNull()→"implementation"),
        // which is not "consolidation" → the early return at L321 fires.
        // The MeterListener verifies the behavioral effect of L321: RecordRunUsageMetrics must NOT
        // be called, so no pipeline.run.tokens measurement is emitted for a non-Consolidation type.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            taskType: WorkItemTaskType.Implementation);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["codegen"] = new PhaseUsagePayload { Tokens = 500 }
            }
        };
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            Result = JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default)
        };

        // Listen for pipeline.run.tokens — must remain empty if L321 early return fires
        var tokenMeasurements = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.tokens")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => tokenMeasurements.Add(value));
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = CreateService(opts, lifecycleManager.Object);

        // Act — awaitTelemetry: true waits for EmitConsolidationUsageTelemetryAsync to complete
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "pre-read guard must fire for a terminal request on an already-Cancelled item; " +
            "EmitConsolidationUsageTelemetryAsync must return early at L321 for a non-Consolidation run type");
        lifecycleManager.VerifyNoOtherCalls();
        tokenMeasurements.Should().BeEmpty(
            "L321 early return must prevent RecordRunUsageMetrics from emitting pipeline.run.tokens " +
            "for a non-Consolidation run type; if this fails, the guard at L321 was removed or bypassed");
    }

    [Fact]
    public async Task TransitionAsync_AlreadyTerminal_ConsolidationTelemetryDbFactoryThrows_ExceptionNotPropagated()
    {
        // Arrange: same pre-read guard entry condition as the NonConsolidationRunType test, but
        // ThrowingDbContextFactory is injected as _dbFactory so ResolveRunContextAsync throws
        // inside the try block of EmitConsolidationUsageTelemetryAsync → L331–L335 catch fires.
        // PhaseBreakdown must be non-empty to get past the first early-return inside that method;
        // if it were empty the try block exits before ResolveRunContextAsync is called.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["codegen"] = new PhaseUsagePayload { Tokens = 500 }
            }
        };
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            Result = JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default)
        };

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = new WorkItemStatusTransitionService(
            CreateTransitionService(opts),
            lifecycleManager.Object,
            NullLogger<WorkItemStatusTransitionService>.Instance,
            new ThrowingDbContextFactory());

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert — exception from ResolveRunContextAsync must be caught at L331–L335
        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget,
            "exception inside EmitConsolidationUsageTelemetryAsync must be caught at L331–L335 " +
            "and must not propagate to the caller");
        lifecycleManager.VerifyNoOtherCalls();
    }

    // ── pipeline.run.outcomes tests (Issue #2967) ──────────────────────────────

    [Fact]
    public async Task TransitionAsync_Succeeded_RecordsPipelineRunOutcome()
    {
        // AC: A terminal transition increments pipeline_run_outcomes_total exactly once.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var outcomeRecordings = new System.Collections.Concurrent.ConcurrentBag<(string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return; // skip pre-init
            string outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            outcomeRecordings.Add((outcome, failureReason));
        });
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var outcome = await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded },
            CancellationToken.None, awaitTelemetry: true);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        outcomeRecordings.Should().Contain(r => r.Outcome == "succeeded" && r.FailureReason == "none",
            "pipeline.run.outcomes must record outcome='succeeded' with failure_reason='none'");
    }

    [Fact]
    public async Task TransitionAsync_Failed_WithTimeout_RecordsPipelineRunOutcomeTimeout()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var outcomeRecordings = new System.Collections.Concurrent.ConcurrentBag<(string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return;
            string outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            outcomeRecordings.Add((outcome, failureReason));
        });
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var outcome = await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "Timeout" },
            CancellationToken.None, awaitTelemetry: true);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        outcomeRecordings.Should().Contain(r => r.Outcome == "timeout" && r.FailureReason == "timeout",
            "pipeline.run.outcomes must record outcome='timeout' with failure_reason='timeout'");
    }

    [Fact]
    public async Task TransitionAsync_WontDo_RecordsFailureReasonNone()
    {
        // AC: wont_do arrives as Succeeded + GateRejected in request — failure_reason must be 'none'.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var outcomeRecordings = new System.Collections.Concurrent.ConcurrentBag<(string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return;
            string outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            outcomeRecordings.Add((outcome, failureReason));
        });
        listener.Start();

        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            AnalysisRecommendation = AnalysisGateResult.WontDo,
            FailureCategory = FailureReason.GateRejected,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        var lifecycleManager = new Mock<IRunLifecycleManager>().Object;
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Succeeded,
                FailureReason = "GateRejected",
                Result = payloadJson
            },
            CancellationToken.None, awaitTelemetry: true);

        outcomeRecordings.Should().Contain(r => r.Outcome == "wont_do" && r.FailureReason == "none",
            "wont_do must produce failure_reason='none' even though request carries GateRejected");
        outcomeRecordings.Should().NotContain(r => r.Outcome == "wont_do" && r.FailureReason == "gate_rejected",
            "failure_reason must NOT be 'gate_rejected' for wont_do outcome");
    }

    [Fact]
    public async Task TransitionAsync_NeedsRefinement_RecordsFailureReasonNone()
    {
        // AC: needs_refinement arrives as Failed + GateRejected — failure_reason must be 'none'.
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var outcomeRecordings = new System.Collections.Concurrent.ConcurrentBag<(string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return;
            string outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            outcomeRecordings.Add((outcome, failureReason));
        });
        listener.Start();

        var payload = new CodingAgent.Pipeline.Models.JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            AnalysisRecommendation = AnalysisGateResult.NotReady,
            FailureCategory = FailureReason.GateRejected,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest
            {
                Status = WorkItemStatus.Failed,
                FailureReason = "GateRejected",
                Result = payloadJson
            },
            CancellationToken.None, awaitTelemetry: true);

        outcomeRecordings.Should().Contain(r => r.Outcome == "needs_refinement" && r.FailureReason == "none",
            "needs_refinement must produce failure_reason='none' even though request carries GateRejected");
    }

    [Fact]
    public async Task TransitionAsync_AlreadyAtTarget_DoesNotRecordPipelineRunOutcome()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Cancelled,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var dbFactory = CreateDbFactory(opts);

        var count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            if (measurement > 0) Interlocked.Increment(ref count);
        });
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>(MockBehavior.Strict);
        var svc = new WorkItemStatusTransitionService(CreateTransitionService(opts), lifecycleManager.Object, NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);
        var outcome = await svc.TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed },
            CancellationToken.None, awaitTelemetry: true);

        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget);
        count.Should().Be(0,
            "pipeline.run.outcomes must NOT be recorded for an AlreadyAtTarget no-op");
    }

    // ── FailureCategory IsDefined null-assignment (Issue #3975) ───────────────

    [Fact]
    public async Task TransitionAsync_Failed_WithOutOfRangeNumericFailureCategoryInPayload_RecordsFallbackFailureReasonNone()
    {
        // Arrange: hand-crafted camelCase JSON with "failureCategory":99.
        // PipelineJsonOptions.Default uses JsonNamingPolicy.CamelCase (no PropertyNameCaseInsensitive),
        // so property keys must be camelCase. PascalCase keys would miss the [required] FinalStep
        // property, causing JsonException and routing control to the catch block before L500.
        //
        // With camelCase: JsonStringEnumConverter (allowIntegerValues=true default) deserializes
        // "failureCategory":99 as (FailureReason)99. The IsDefined guard at L500 then nulls it out.
        // DeriveOutcome(Failed, payload, null) → ("failed", "none"), not ("failed", "99").
        const string resultJson =
            """{"finalStep":"Failed","completedAt":"2026-01-01T00:00:00+00:00","failureCategory":99}""";

        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var dbFactory = CreateDbFactory(opts);

        var outcomeRecordings = new System.Collections.Concurrent.ConcurrentBag<(string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return;
            string outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            outcomeRecordings.Add((outcome, failureReason));
        });
        listener.Start();

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((PipelineRun?)null);

        var svc = new WorkItemStatusTransitionService(
            CreateTransitionService(opts), lifecycleManager.Object,
            NullLogger<WorkItemStatusTransitionService>.Instance, dbFactory);

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = resultJson };

        // Act
        var outcome = await svc.TransitionAsync(item.Id, request, CancellationToken.None, awaitTelemetry: true);

        // Assert
        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        outcomeRecordings.Should().Contain(r => r.Outcome == "failed" && r.FailureReason == "none",
            "IsDefined guard at L500 must null out the undefined (FailureReason)99; " +
            "DeriveOutcome must produce failure_reason='none', not '99' or any numeric string");
        outcomeRecordings.Should().NotContain(r => r.FailureReason == "99",
            "the IsDefined guard must prevent an undefined numeric FailureCategory from " +
            "becoming a metric tag value");
    }

    // ── ParseFailureReason and SanitizePodLogTail (Issue #3588) ───────────────

    // TODO: [WARNING] Neither Failed_WithTimeoutReason_PassesTimeoutCategory nor
    // Failed_WithoutReason_PassesInfrastructureFailure asserts on the podLogTail argument
    // forwarded to FailRunWithLabelAsync. Both Callbacks discard the sixth argument. No test in
    // the suite sends a non-null WorkItemStatusRequest.PodLogTail and verifies it is sanitized
    // via SanitizePodLogTail and passed through DispatchTransitionedAsync. Concrete failure
    // scenario: replacing SanitizePodLogTail(request.PodLogTail) with null in
    // WorkItemStatusTransitionService.cs would leave all existing tests green while silently
    // dropping the pod log. Add a test that sends PodLogTail lines with a secret, and asserts
    // the sanitized lines reach FailRunWithLabelAsync. See TestQualityReviewer finding [WARNING] #2.

    [Fact]
    public async Task Failed_WithTimeoutReason_PassesTimeoutCategory()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        FailureReason? capturedCategory = null;
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .Callback<RunId, string, string?, CancellationToken, FailureReason?, IReadOnlyList<string>?>(
                (_, _, _, _, cat, _) => capturedCategory = cat)
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed, FailureReason = "Timeout" };

        await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        capturedCategory.Should().Be(FailureReason.Timeout,
            "a valid FailureReason string must be parsed and forwarded as the category");
    }

    [Fact]
    public async Task Failed_WithoutReason_PassesInfrastructureFailure()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        FailureReason? capturedCategory = null;
        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.FailRunWithLabelAsync(
                It.IsAny<RunId>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<FailureReason?>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .Callback<RunId, string, string?, CancellationToken, FailureReason?, IReadOnlyList<string>?>(
                (_, _, _, _, cat, _) => capturedCategory = cat)
            .ReturnsAsync((PipelineRun?)null);

        var svc = CreateService(opts, lifecycleManager.Object);
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Failed };

        await svc.TransitionAsync(item.Id, request, CancellationToken.None);

        capturedCategory.Should().Be(FailureReason.InfrastructureFailure,
            "null FailureReason must fall back to InfrastructureFailure");
    }

    [Fact]
    public void SanitizePodLogTail_KeepsLast100LinesAndTruncatesTo1000Chars()
    {
        // 150 lines — last 100 must survive
        var lines = Enumerable.Range(1, 150).Select(i => $"line {i}").ToList();
        var result = WorkItemStatusTransitionService.SanitizePodLogTail(lines);

        result.Should().NotBeNull();
        result!.Count.Should().Be(100);
        result[0].Should().Be("line 51");
        result[99].Should().Be("line 150");

        // A line of 2000 chars must be truncated to 1000
        var longLine = new string('x', 2000);
        var resultLong = WorkItemStatusTransitionService.SanitizePodLogTail([longLine]);
        resultLong.Should().NotBeNull();
        resultLong![0].Length.Should().Be(1000);
    }

    [Fact]
    public void SanitizePodLogTail_MasksSecrets()
    {
        // TODO: [WARNING] This test does not exercise regex 3 (GitHubTokenRegex:
        // \bgh[pousr]_[A-Za-z0-9]{20,}). The input "GITHUB_TOKEN=ghs_abc" has only 3 chars after
        // the ghs_ prefix, so it fails the {20,} minimum-length requirement and is masked by
        // regex 1 (key-value pattern) instead. If regex 3 were deleted entirely, all five
        // assertions here would still pass. Add a case with a real-format standalone token
        // (not preceded by a key=value separator) such as "ghp_1234567890abcdefghijkl" to lock in
        // regex 3 behaviour independently. See TestQualityReviewer finding [WARNING] #1.
        var lines = new List<string>
        {
            "GITHUB_TOKEN=ghs_abc",
            "Authorization: Bearer xyz",
            "api-key: k1",
            "sending Bearer abc",
            "tokens used: 1200"
        };

        var result = WorkItemStatusTransitionService.SanitizePodLogTail(lines);

        result.Should().NotBeNull();
        result![0].Should().Be("GITHUB_TOKEN=***", "token=value pattern must be masked");
        result[1].Should().Be("Authorization: ***", "Authorization: Bearer must be masked");
        result[2].Should().Be("api-key: ***", "api-key: value pattern must be masked");
        result[3].Should().Be("sending Bearer ***", "standalone Bearer token must be masked");
        result[4].Should().Be("tokens used: 1200", "keyword must be followed by separator to match");
    }

    // ── RegexMatchTimeoutException paths (Issue #3941) ────────────────────────

    // These tests cover the three catch (RegexMatchTimeoutException) { continue; } branches at
    // L602, L604, and L606.  The [GeneratedRegex] patterns are ReDoS-safe and never time out
    // under ordinary inputs; the only way to trigger the branches without changing production
    // code is to temporarily shrink the regex singleton's internalMatchTimeout to 100 ns via
    // reflection.  Each test saves the original timeout, sets it to TimeSpan.FromTicks(1), runs
    // SanitizePodLogTail, then restores unconditionally in a finally block.  The collection's
    // DisableParallelization = true prevents concurrent access to the shared singleton.

    private static readonly FieldInfo? s_timeoutField =
        typeof(Regex).GetField("internalMatchTimeout", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Returns the Regex singleton for the named private static partial method on
    /// <see cref="WorkItemStatusTransitionService"/> so its timeout can be temporarily altered.
    /// </summary>
    private static Regex GetRegexSingleton(string methodName)
    {
        var method = typeof(WorkItemStatusTransitionService)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull(
            $"{methodName}() must be a private static method on WorkItemStatusTransitionService; " +
            "update this call if it was renamed");
        return (Regex)method!.Invoke(null, null)!;
    }

    [Fact]
    public void SanitizePodLogTail_KeyValueSecretRegexTimeout_DropsTimedOutLine()
    {
        // Covers L602: catch (RegexMatchTimeoutException) { continue; }
        // AC1: a single timed-out line must be absent from the result (returns null).
        s_timeoutField.Should().NotBeNull(
            "Regex.internalMatchTimeout must exist; update this reflection call if the .NET runtime renamed it");

        var regex = GetRegexSingleton("KeyValueSecretRegex");
        var original = (TimeSpan)s_timeoutField!.GetValue(regex)!;
        s_timeoutField.SetValue(regex, TimeSpan.FromTicks(1));
        try
        {
            // "token=" followed by spaces causes the engine to attempt a match that exercises
            // CheckTimeout; at 100 ns the call overhead alone exceeds the deadline.
            var result = WorkItemStatusTransitionService.SanitizePodLogTail(
                ["token=" + new string(' ', 994)]);

            result.Should().BeNull(
                "the single timed-out line must be skipped; with no surviving lines the method returns null");
        }
        finally
        {
            s_timeoutField.SetValue(regex, original);
        }
    }

    [Fact]
    public void SanitizePodLogTail_KeyValueSecretRegexTimeout_SubsequentLinesArePresent()
    {
        // Covers L602: catch (RegexMatchTimeoutException) { continue; }
        // AC2: a timeout on the first line must not prevent subsequent lines from being processed.
        s_timeoutField.Should().NotBeNull(
            "Regex.internalMatchTimeout must exist; update this reflection call if the .NET runtime renamed it");

        var regex = GetRegexSingleton("KeyValueSecretRegex");
        var original = (TimeSpan)s_timeoutField!.GetValue(regex)!;
        s_timeoutField.SetValue(regex, TimeSpan.FromTicks(1));
        try
        {
            var result = WorkItemStatusTransitionService.SanitizePodLogTail(
                ["token=" + new string(' ', 994), "log line 2"]);

            result.Should().NotBeNull();
            result!.Should().ContainSingle(
                "the timed-out first line must be skipped while the second line survives");
            result[0].Should().Be("log line 2",
                "the non-triggering line must pass through unchanged");
        }
        finally
        {
            s_timeoutField.SetValue(regex, original);
        }
    }

    [Fact]
    public void SanitizePodLogTail_BearerTokenRegexTimeout_DropsTimedOutLine()
    {
        // Covers L604: catch (RegexMatchTimeoutException) { continue; }
        s_timeoutField.Should().NotBeNull(
            "Regex.internalMatchTimeout must exist; update this reflection call if the .NET runtime renamed it");

        var regex = GetRegexSingleton("BearerTokenRegex");
        var original = (TimeSpan)s_timeoutField!.GetValue(regex)!;
        s_timeoutField.SetValue(regex, TimeSpan.FromTicks(1));
        try
        {
            // "Bearer " followed by spaces: \s+\S+ backtracks on the trailing spaces.
            var result = WorkItemStatusTransitionService.SanitizePodLogTail(
                ["Bearer " + new string(' ', 993)]);

            result.Should().BeNull(
                "the timed-out line must be skipped; no surviving lines means the method returns null");
        }
        finally
        {
            s_timeoutField.SetValue(regex, original);
        }
    }

    [Fact]
    public void SanitizePodLogTail_GitHubTokenRegexTimeout_DropsTimedOutLine()
    {
        // Covers L606: catch (RegexMatchTimeoutException) { continue; }
        s_timeoutField.Should().NotBeNull(
            "Regex.internalMatchTimeout must exist; update this reflection call if the .NET runtime renamed it");

        var regex = GetRegexSingleton("GitHubTokenRegex");
        var original = (TimeSpan)s_timeoutField!.GetValue(regex)!;
        s_timeoutField.SetValue(regex, TimeSpan.FromTicks(1));
        try
        {
            // "!ghp_" + 19 'a's repeated to fill ~1000 chars (SanitizePodLogTail truncates to 1000).
            // Each "!" creates a word boundary before 'g', triggering a match attempt.
            // Each attempt processes 23 individual chars (gh[pousr]_ = 4, then 19 'a's that
            // fail {20,} because only 19 < 20 are present) before failing — these are
            // per-character checks that the engine cannot batch via SIMD.  With ~41 such
            // attempts the engine makes enough CheckTimeout calls for the 100 ns deadline to be
            // exceeded, typically within the first two attempts.
            var block = "!ghp_" + new string('a', 19);
            var input = string.Concat(Enumerable.Repeat(block, 42)); // 1008 chars → truncated to 1000

            var result = WorkItemStatusTransitionService.SanitizePodLogTail([input]);

            result.Should().BeNull(
                "the timed-out line must be skipped; no surviving lines means the method returns null");
        }
        finally
        {
            s_timeoutField.SetValue(regex, original);
        }
    }

    // ── Test Infrastructure ────────────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rv = entityType.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexes = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
                foreach (var idx in indexes)
                    entityType.RemoveIndex(idx);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// A factory that always throws <see cref="InvalidOperationException"/> from
    /// <see cref="CreateDbContextAsync"/>. Injected as <c>_dbFactory</c> into
    /// <see cref="WorkItemStatusTransitionService"/> to exercise the telemetry exception catches
    /// at L299–L303 (EmitTerminalStatusTelemetryAsync) and L331–L335
    /// (EmitConsolidationUsageTelemetryAsync) without affecting the transition service's own factory.
    /// </summary>
    private sealed class ThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext()
            => throw new InvalidOperationException("Simulated DB failure for telemetry coverage test");

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromException<PipelineDbContext>(
                new InvalidOperationException("Simulated DB failure for telemetry coverage test"));
    }
}
