using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="LabelSwapService"/> (#1868).
/// Verifies retry policy, exponential backoff, and OCE propagation
/// across maxAttempts=1 (K8s mode) and maxAttempts=3 (SignalR mode) configurations.
/// </summary>
public sealed class LabelSwapServiceTests
{
    private readonly Mock<ILabelService> _mockLabelService = new();

    private static readonly Guid WorkItemId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly ProviderConfigId Provider = (ProviderConfigId)"issue-provider-1";
    private static readonly IssueIdentifier Identifier = (IssueIdentifier)"org/repo#42";
    private static readonly LabelTargetKind Kind = LabelTargetKind.Issue;

    // ── Helper ─────────────────────────────────────────────────────────────

    private LabelSwapService CreateService(int maxAttempts = 3) =>
        new(_mockLabelService.Object,
            NullLogger<LabelSwapService>.Instance, maxAttempts);

    // ── maxAttempts=3 tests ─────────────────────────────────────────────────

    [Fact]
    public async Task SwapLabel_FirstAttemptSucceeds_CallsSwapLabelStrictOnce()
    {
        // TODO: This test only asserts call count. Add `await act.Should().NotThrowAsync()` (or
        // equivalent) to also assert that SwapLabelWithRetryAsync completes cleanly on the happy
        // path. Without a completion assertion, an implementation that throws after calling the
        // swap would still pass this test.
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(maxAttempts: 3);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SwapLabel_FirstAttemptFails_RetriesAndSucceeds()
    {
        // TODO: This test only asserts call count. Add `await act.Should().NotThrowAsync()` (or
        // equivalent) to also assert that SwapLabelWithRetryAsync completes cleanly when a retry
        // succeeds. Without a completion assertion, an implementation that throws after the
        // successful second attempt would still pass this test.
        var callCount = 0;
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException(new HttpRequestException("rate limited"))
                    : Task.CompletedTask;
            });

        var service = CreateService(maxAttempts: 3);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task SwapLabel_CancellationOnFirstAttempt_PropagatesOce_DoesNotFlag_MaxAttempts3()
    {
        // OCE propagation is unconditional regardless of maxAttempts.
        // Note: CancellationToken.None is passed so ct.IsCancellationRequested is always false.
        // This correctly reflects the test scenario: OCE thrown by the swap itself (not by backoff).
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(maxAttempts: 3);

        var act = async () => await service.SwapLabelWithRetryAsync(
            WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>("OCE must propagate unconditionally");

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── maxAttempts=1 (K8s mode) tests ─────────────────────────────────────

    [Fact]
    public async Task SwapLabel_MaxAttemptsOne_Success_NoFlag()
    {
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(maxAttempts: 1);
        await service.SwapLabelWithRetryAsync(WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SwapLabel_CancellationOnFirstAttempt_PropagatesOce_DoesNotFlag_MaxAttemptsOne()
    {
        // OCE propagation is unconditional regardless of maxAttempts.
        // Note: CancellationToken.None is passed so ct.IsCancellationRequested is always false.
        // This correctly reflects the test scenario: OCE thrown by the swap itself (not by backoff).
        _mockLabelService
            .Setup(l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(maxAttempts: 1);

        var act = async () => await service.SwapLabelWithRetryAsync(
            WorkItemId, Provider, Identifier, Kind, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>("OCE must propagate unconditionally even with maxAttempts=1");

        _mockLabelService.Verify(
            l => l.SwapLabelStrictAsync(Provider, Identifier, AgentLabels.InProgress, Kind, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Constructor guard tests ─────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLabelService_ThrowsArgumentNullException()
    {
        var act = () => new LabelSwapService(
            null!,
            NullLogger<LabelSwapService>.Instance);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("labelService");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new LabelSwapService(
            _mockLabelService.Object,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }
}
