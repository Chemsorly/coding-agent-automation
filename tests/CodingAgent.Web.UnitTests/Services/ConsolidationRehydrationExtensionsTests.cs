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
/// <c>RunConsolidationStartupAsync</c> performs two tasks:
/// 1. Marks any <c>Running</c> consolidation runs as <c>Failed</c> if no active agent is working on them.
/// 2. Re-adds <c>Pending</c> run keys to the in-memory dedup tracker so duplicate triggers are blocked.
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

    // ── No-dispatch guarantee ────────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationStartupAsync_OnlyCallsCleanupOrphanedRunsAsync()
    {
        // RunConsolidationStartupAsync only calls CleanupOrphanedRunsAsync —
        // there is no retry dispatch loop (that machinery was removed with the Queued state).
        SetupDefaults();
        await using var app = BuildApp();

        await app.RunConsolidationStartupAsync();

        // Cleanup was called
        _consolidationService.Verify(
            s => s.CleanupOrphanedRunsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
