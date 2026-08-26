using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for JobDeduplicationGuardService — covers agent selection and label matching.
/// Queue methods (EnqueueJob, DequeueForAgent, etc.) were deleted in T18 (arch-audit 2026-08-22)
/// as they were provably no-ops; those test cases have been removed here as well.
/// </summary>
public class JobDeduplicationGuardServiceExtendedTests
{
    private readonly AgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<ILogger> _mockLogger;

    public JobDeduplicationGuardServiceExtendedTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);
    }

    // ── SelectAgent ─────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_NoIdleAgents_ReturnsNull()
    {
        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_IdleAgentWithMatchingLabels_ReturnsAgent()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "dotnet", "linux" });

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().NotBeNull();
        result!.AgentId.Value.Should().Be("agent-1");
    }

    [Fact]
    public void SelectAgent_IdleAgentWithoutMatchingLabels_ReturnsNull()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "java" });

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_EmptyRequiredLabels_MatchesAnyAgent()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "java" });

        var result = _dispatcher.SelectAgent(Array.Empty<string>());

        result.Should().NotBeNull();
    }

    [Fact]
    public void SelectAgent_MultipleIdleAgents_SelectsLongestIdle()
    {
        var entry1 = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        entry1.LastJobCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var entry2 = RegisterAgent("agent-2", "conn-2", new[] { "dotnet" });
        entry2.LastJobCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result!.AgentId.Value.Should().Be("agent-1"); // Idle longer
    }

    [Fact]
    public void SelectAgent_DisabledAgent_Skipped()
    {
        var entry = RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        entry.Disabled = true;

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_BusyAgent_NotSelected()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        var result = _dispatcher.SelectAgent(new[] { "dotnet" });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_SetsBusySinceOnReservation()
    {
        RegisterAgent("agent-1", "conn-1", new[] { "dotnet" });

        var before = DateTimeOffset.UtcNow;
        var result = _dispatcher.SelectAgent(new[] { "dotnet" });
        var after = DateTimeOffset.UtcNow;

        result.Should().NotBeNull();
        result!.BusySince.Should().NotBeNull();
        result.BusySince!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── ResolveRequiredLabels ────────────────────────────────────────────

    [Fact]
    public void ResolveRequiredLabels_NullRepoConfig_UsesDefaults()
    {
        var config = new PipelineConfiguration();

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(null, config);

        labels.Should().NotBeNull();
    }

    [Fact]
    public void ResolveRequiredLabels_RepoConfigWithLabels_UsesRepoLabels()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>(),
            RequiredLabels = new[] { "custom-label" }
        };
        var config = new PipelineConfiguration();

        var labels = JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, config);

        labels.Should().Contain("custom-label");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private AgentEntry RegisterAgent(string agentId, string connectionId, IReadOnlyList<string> labels)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = labels
        }, connectionId);
    }
}
