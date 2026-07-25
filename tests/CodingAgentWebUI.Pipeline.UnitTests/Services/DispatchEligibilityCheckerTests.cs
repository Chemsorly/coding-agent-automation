using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Xunit;
using static CodingAgentWebUI.Orchestration.Dispatch.DispatchService;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Pure unit tests for <see cref="DispatchEligibilityChecker"/>.
/// No database, K8s, or SignalR dependencies — validates eligibility logic in isolation.
/// </summary>
public class DispatchEligibilityCheckerTests
{
    // ── Concurrency Limit ────────────────────────────────────────────────

    [Fact]
    public async Task CheckEligibility_AtConcurrencyLimit_ReturnsAtConcurrencyLimit()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 2 });

        var item = CreateItem("kiro,dotnet");
        // Dictionary key matches item.AgentSelector (as it would from DB GROUP BY)
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 2 };

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.AtConcurrencyLimit);
    }

    [Fact]
    public async Task CheckEligibility_BelowConcurrencyLimit_ReturnsEligible()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 2 });

        var item = CreateItem("kiro,dotnet");
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 1 };

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.Eligible);
        result.Template.Should().NotBeNull();
        result.Template!.Image.Should().Be("ghcr.io/agent:latest");
        // TODO: Assert result.EffectiveSelector matches expected value — currently a wrong EffectiveSelector on the direct-match path would go undetected
    }

    [Fact]
    public async Task CheckEligibility_ZeroConcurrencyLimit_AlwaysEligible()
    {
        // maxConcurrent = 0 means no limit
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 0 });

        var item = CreateItem("kiro,dotnet");
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 100 };

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.Eligible);
        // TODO: Assert result.EffectiveSelector and result.IsKiroAgent to strengthen this test — currently only verifies concurrency bypass
    }

    // ── Template Resolution ──────────────────────────────────────────────

    [Fact]
    public async Task CheckEligibility_NoTemplateFound_ReturnsNoTemplate()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" });

        var item = CreateItem("unknown-label");
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.NoTemplate);
        result.ErrorMessage.Should().Contain("No job template for selector: unknown-label");
    }

    [Fact]
    public async Task CheckEligibility_ProfileFallback_ReturnsEligibleWithResolvedSelector()
    {
        // "dotnet" has no direct template match, but profile resolves it to "dotnet,kiro"
        var mockProfileStore = new Mock<IAgentProfileStore>();
        mockProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-1",
                    DisplayName = "Kiro Dotnet",
                    AgentProviderConfigId = "agent-provider-1",
                    Enabled = true,
                    MatchLabels = ["dotnet", "kiro"],
                    Priority = 1
                }
            });

        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            agentProfileStore: mockProfileStore.Object);

        var item = CreateItem("dotnet");
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.Eligible);
        result.EffectiveSelector.Should().Be("dotnet,kiro");
        result.Template.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckEligibility_ProfileFallback_ResolvedSelectorAtLimit_ReturnsAtConcurrencyLimit()
    {
        var mockProfileStore = new Mock<IAgentProfileStore>();
        mockProfileStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-1",
                    DisplayName = "Kiro Dotnet",
                    AgentProviderConfigId = "agent-provider-1",
                    Enabled = true,
                    MatchLabels = ["dotnet", "kiro"],
                    Priority = 1
                }
            });

        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 1 },
            agentProfileStore: mockProfileStore.Object);

        var item = CreateItem("dotnet");
        var concurrency = new Dictionary<string, int> { ["dotnet,kiro"] = 1 };

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.AtConcurrencyLimit);
    }

    // ── PVC Availability ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckEligibility_KiroAgentNoPvcsAvailable_ReturnsNoPvcAvailable()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" });

        var item = CreateItem("kiro,dotnet");
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 0, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.NoPvcAvailable);
    }

    [Fact]
    public async Task CheckEligibility_KiroAgentWithPvcsAvailable_ReturnsEligible()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" });

        var item = CreateItem("kiro,dotnet");
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 2, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.Eligible);
        result.IsKiroAgent.Should().BeTrue();
    }

    [Fact]
    public async Task CheckEligibility_NonKiroAgentWithNoPvcs_ReturnsEligible()
    {
        var checker = CreateChecker(
            imageMapping: new() { ["opencode,python"] = "ghcr.io/opencode:latest" },
            providerType: "opencode");

        var item = CreateItem("opencode,python");
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 0, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.Eligible);
        result.IsKiroAgent.Should().BeFalse();
    }

    // ── Null AgentProfileStore ───────────────────────────────────────────

    [Fact]
    public async Task CheckEligibility_NullAgentProfileStore_NoFallback_ReturnsNoTemplate()
    {
        // No profile store means no fallback resolution
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            agentProfileStore: null);

        var item = CreateItem("dotnet"); // No direct match
        var concurrency = new Dictionary<string, int>();

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.NoTemplate);
    }

    // ── Consolidation Items Use Shared Checker ───────────────────────────

    [Fact]
    public async Task CheckEligibility_ConsolidationItem_AtConcurrencyLimit_ReturnsAtConcurrencyLimit()
    {
        // Proves the shared checker handles consolidation items identically to regular items
        var checker = CreateChecker(
            imageMapping: new() { ["dotnet,kiro"] = "ghcr.io/agent:latest" },
            maxConcurrent: new() { ["dotnet,kiro"] = 1 });

        var item = CreateItem("kiro,dotnet", taskType: WorkItemTaskType.Consolidation);
        // Dictionary key matches item.AgentSelector (as built from DB GROUP BY)
        var concurrency = new Dictionary<string, int> { ["kiro,dotnet"] = 1 };

        var result = await checker.CheckEligibilityAsync(item, concurrency, availablePvcCount: 1, CancellationToken.None);

        result.Outcome.Should().Be(EligibilityOutcome.AtConcurrencyLimit);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DispatchEligibilityChecker CreateChecker(
        Dictionary<string, string> imageMapping,
        Dictionary<string, int>? maxConcurrent = null,
        IAgentProfileStore? agentProfileStore = null,
        string providerType = "kiro")
    {
        var templateProvider = BuildTemplateProvider(imageMapping, maxConcurrent, providerType);
        return new DispatchEligibilityChecker(templateProvider, agentProfileStore);
    }

    private static JobTemplateStore BuildTemplateProvider(
        Dictionary<string, string> imageMapping,
        Dictionary<string, int>? maxConcurrent = null,
        string providerType = "kiro")
    {
        var normalizedMaxConcurrent = maxConcurrent?.ToDictionary(
            kv => JobTemplateStore.NormalizeLabels(kv.Key), kv => kv.Value);

        var templates = imageMapping.Select(kv => new JobTemplate
        {
            Labels = kv.Key,
            Image = kv.Value,
            ProviderType = kv.Key.Contains("kiro") ? "kiro" : providerType,
            MaxConcurrent = normalizedMaxConcurrent?.GetValueOrDefault(
                JobTemplateStore.NormalizeLabels(kv.Key), 0) ?? 0
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateStore.LoadFromJson(json);
    }

    private static PendingWorkItemProjection CreateItem(
        string agentSelector,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation)
    {
        return new PendingWorkItemProjection
        {
            Id = Guid.NewGuid(),
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            TaskType = taskType,
            ProjectId = null,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "provider-1"
        };
    }
}
