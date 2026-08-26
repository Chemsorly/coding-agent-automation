using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for LabelSwapService.SwapLabelWithRetryAsync.
/// Covers: success on first attempt, retry on failure, exhaustion, cancellation propagation.
/// Uses maxAttempts=1 for fast tests (no delays) except where retries are tested.
/// </summary>
public sealed class LabelSwapServiceTests
{
    private static readonly Guid WorkItemId = Guid.NewGuid();
    private static readonly ProviderConfigId ProviderId = new("github");
    private static readonly IssueIdentifier IssueId = new("GH-42");

    private static LabelSwapService Create(Mock<ILabelService> labelService, int maxAttempts = 1)
        => new(labelService.Object, NullLogger<LabelSwapService>.Instance, maxAttempts);

    // ── Success on first attempt ──────────────────────────────────────────

    [Fact]
    public async Task SwapLabelWithRetryAsync_OnSuccess_CallsSwapLabelStrictOnce()
    {
        var labelService = new Mock<ILabelService>();
        labelService.Setup(l => l.SwapLabelStrictAsync(
            ProviderId, IssueId, AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = Create(labelService);
        await sut.SwapLabelWithRetryAsync(WorkItemId, ProviderId, IssueId, LabelTargetKind.Issue, CancellationToken.None);

        labelService.Verify(l => l.SwapLabelStrictAsync(
            ProviderId, IssueId, AgentLabels.InProgress, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Single attempt, failure ───────────────────────────────────────────

    [Fact]
    public async Task SwapLabelWithRetryAsync_OnFailure_DoesNotThrow_WithMaxAttempts1()
    {
        var labelService = new Mock<ILabelService>();
        labelService.Setup(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider error"));

        var sut = Create(labelService, maxAttempts: 1);

        // Should not throw — failure is swallowed after max retries
        var act = () => sut.SwapLabelWithRetryAsync(WorkItemId, ProviderId, IssueId, LabelTargetKind.Issue, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Retries: succeeds on 2nd attempt ─────────────────────────────────

    [Fact]
    public async Task SwapLabelWithRetryAsync_SucceedsOnSecondAttempt()
    {
        var callCount = 0;
        var labelService = new Mock<ILabelService>();
        labelService.Setup(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("first attempt fails");
                return Task.CompletedTask;
            });

        // Use maxAttempts=2 but we need zero delay — patch by using Task.Delay(0)
        // The delay in LabelSwapService is real, so keep maxAttempts small for test speed
        var sut = Create(labelService, maxAttempts: 2);

        // This will do a real 200ms delay on retry — acceptable for a unit test
        await sut.SwapLabelWithRetryAsync(WorkItemId, ProviderId, IssueId, LabelTargetKind.Issue, CancellationToken.None);

        labelService.Verify(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Cancellation propagates ───────────────────────────────────────────

    [Fact]
    public async Task SwapLabelWithRetryAsync_WhenCancelled_ThrowsOperationCancelledException()
    {
        using var cts = new CancellationTokenSource();
        var labelService = new Mock<ILabelService>();
        labelService.Setup(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = Create(labelService, maxAttempts: 3);

        var act = () => sut.SwapLabelWithRetryAsync(WorkItemId, ProviderId, IssueId, LabelTargetKind.Issue, cts.Token);

        // OperationCanceledException propagates (not swallowed)
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Only called once — cancellation stops retries
        labelService.Verify(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Exhaustion: all attempts fail ─────────────────────────────────────

    [Fact]
    public async Task SwapLabelWithRetryAsync_AllAttemptsFail_DoesNotThrow()
    {
        var labelService = new Mock<ILabelService>();
        labelService.Setup(l => l.SwapLabelStrictAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
            It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = Create(labelService, maxAttempts: 1);

        var act = () => sut.SwapLabelWithRetryAsync(WorkItemId, ProviderId, IssueId, LabelTargetKind.Issue, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullLabelService_Throws()
    {
        var act = () => new LabelSwapService(null!, NullLogger<LabelSwapService>.Instance);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new LabelSwapService(new Mock<ILabelService>().Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
