using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationRehydrationExtensions.RunConsolidationStartupAsync"/>.
///
/// After issue #2566, the startup dispatch-rehydration loop was removed. Pending runs are now
/// poller-owned: ConsolidationRetryBackgroundService handles any queued runs after startup.
/// RunConsolidationStartupAsync only performs orphan cleanup.
///
/// Uses a raw <c>WebApplication.CreateBuilder()</c> host to avoid Program.cs fast-fail
/// env-var checks. All services consumed by the extension method are registered as mocks.
/// </summary>
public sealed class ConsolidationRehydrationExtensionsTests
{
    // ── Test fixture ──────────────────────────────────────────────────────

    private readonly Mock<IConsolidationService> _consolidationService = new();
    private readonly Mock<IPipelineApiAgentClient> _apiAgentClient = new();

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton(_consolidationService.Object);
        builder.Services.AddSingleton(_apiAgentClient.Object);

        return builder.Build();
    }

    private void SetupDefaults(IReadOnlyList<AgentEntryDto>? agents = null)
    {
        _apiAgentClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(agents ?? Array.Empty<AgentEntryDto>());

        _consolidationService
            .Setup(s => s.CleanupOrphanedRunsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── Guard tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_NullApp_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ConsolidationRehydrationExtensions
                .RunConsolidationStartupAsync(null!));
    }

    // ── Orphan cleanup ────────────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_NoLiveAgents_CallsCleanupWithEmptySet()
    {
        SetupDefaults(agents: Array.Empty<AgentEntryDto>());
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync();

        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set => set.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_AgentWithActiveJob_ExcludesItFromOrphanSet()
    {
        // An agent actively running a job should NOT be treated as orphaned
        var activeAgent = AgentEntryDtoFactory.From(new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-1",
            Hostname = "k8s-pod",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            ActiveJobId = "job-abc-123"
        }, run: null);
        SetupDefaults(agents: [activeAgent]);
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync();

        // The active job ID should NOT be in the orphan set — runs with that job ID are preserved
        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set =>
                    set.Count == 1 && set.Contains("job-abc-123")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunConsolidationStartupAsync_ApiAgentClientThrows_TreatsAllRunsAsOrphaned()
    {
        // If the API is unreachable at startup, all running runs become orphan candidates
        _apiAgentClient
            .Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        _consolidationService
            .Setup(s => s.CleanupOrphanedRunsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var app = BuildApp();

        // Should not throw — exception is swallowed and treated as "no live agents"
        await app.RunConsolidationStartupAsync();

        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.Is<IReadOnlyCollection<string>>(set => set.Count == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── No-dispatch guarantee (startup dispatch loop removed in #2566) ────

    [Fact]
    public async Task RunConsolidationStartupAsync_DoesNotCallRehydrateQueuedRunsAsync()
    {
        // The startup dispatch loop was removed in #2566 — queued runs are now owned by
        // ConsolidationRetryBackgroundService, not startup rehydration.
        SetupDefaults();
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync();

        _consolidationService.Verify(
            s => s.RehydrateQueuedRunsAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "startup rehydration dispatch loop was removed in #2566");
    }
}
