using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Work page — backlog label chips, stat-strip secondary data.
/// </summary>
public class WorkPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItems = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IDependencyChecker> _mockDependencyChecker = new();

    public WorkPageComponentTests()
    {
        // BlockedIssuesService is a concrete class — create a real instance with mocked deps.
        var blockedIssuesService = new BlockedIssuesService(
            _mockConfigClient.Object,
            _mockProviderFactory.Object,
            _mockDependencyChecker.Object);

        var cockpitState = new CockpitState();

        Services.AddSingleton(_mockWorkItems.Object);
        Services.AddSingleton(blockedIssuesService);
        Services.AddSingleton(cockpitState);
    }

    private void SetupWorkItems(
        IReadOnlyList<ActiveWorkItemDto>? active = null,
        IReadOnlyList<PendingWorkItemDto>? pending = null)
    {
        _mockWorkItems
            .Setup(w => w.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(active ?? []);
        _mockWorkItems
            .Setup(w => w.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending ?? []);
        _mockWorkItems
            .Setup(w => w.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task WhenBacklogHasLabels_ShouldRenderLabelChipsOnEachRow()
    {
        SetupWorkItems();
        // Set up the config to return no templates so BlockedIssuesService returns the
        // BacklogIssue list we inject into the test via a separate mock path.
        // Instead, we test the rendering path by rendering the page with known backlog data
        // and asserting the label chips appear. Since BacklogIssue is created by the service,
        // we cannot directly inject them without going through the full service call.
        // The cleanest path: supply a config that returns a template + provider + issues.
        var issue = new IssueSummary
        {
            Identifier = "10",
            Title = "Feature work",
            Labels = new[] { "agent:next", "bug" },
            Description = "",
            Url = "https://x/issues/10"
        };

        var pagedResult = new PagedResult<IssueSummary>
        {
            Items = new[] { issue },
            Page = 1, PageSize = 20, HasMore = false
        };

        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true
        };

        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var cut = Render<Work>();

        // Wait for OnAfterRenderAsync to trigger LoadBacklogAsync
        // TODO: Fixed Task.Delay(100) is timing-dependent and can be flaky on slow CI runners.
        // If the async chain hasn't completed within 100 ms the test passes without verifying anything;
        // if it takes longer the WaitForState below compensates, but the Delay adds unnecessary latency.
        // Remove the Delay and rely solely on cut.WaitForState / cut.WaitForAssertion for synchronisation.
        await cut.InvokeAsync(async () => await Task.Delay(100));
        cut.WaitForState(() => !cut.Markup.Contains("checking…"), timeout: TimeSpan.FromSeconds(3));

        var labelChips = cut.FindAll(".sidebar-label");
        Assert.True(labelChips.Count >= 2, $"Expected at least 2 label chips, found {labelChips.Count}");
        Assert.Contains(labelChips, l => l.TextContent.Trim() == "agent:next");
        Assert.Contains(labelChips, l => l.TextContent.Trim() == "bug");
    }

    [Fact]
    public async Task WhenBacklogHasAgentLabel_ShouldApplyColorClass()
    {
        SetupWorkItems();

        var issue = new IssueSummary
        {
            Identifier = "20",
            Title = "Agent issue",
            Labels = new[] { "agent:next" },
            Description = "",
            Url = "https://x/issues/20"
        };
        var pagedResult = new PagedResult<IssueSummary>
        {
            Items = new[] { issue },
            Page = 1, PageSize = 20, HasMore = false
        };

        var template = new PipelineJobTemplate
        {
            Id = "t2", Name = "T", IssueProviderId = "prov2", RepoProviderId = "repo2", Enabled = true
        };

        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov2", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var cut = Render<Work>();

        // TODO: Fixed Task.Delay(100) is timing-dependent — see same note in WhenBacklogHasLabels test.
        // Rely solely on WaitForState for synchronisation.
        await cut.InvokeAsync(async () => await Task.Delay(100));
        cut.WaitForState(() => !cut.Markup.Contains("checking…"), timeout: TimeSpan.FromSeconds(3));

        var agentNextChip = cut.FindAll(".sidebar-label")
            .FirstOrDefault(l => l.TextContent.Trim() == "agent:next");
        Assert.NotNull(agentNextChip);
        // GetLabelClass("agent:next") returns "label-agent-next"
        Assert.Contains("label-agent-next", agentNextChip.GetAttribute("class") ?? "");
    }

    [Fact]
    public void WhenActiveItemsExist_ShouldShowOldestActiveAgeInStatStrip()
    {
        // DispatchedAt 2 hours ago — OldestActiveAge() should return "oldest: 2h ago"
        var dispatchedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var active = new List<ActiveWorkItemDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Status = WorkItemStatus.Running,
                DispatchedAt = dispatchedAt,
                AgentSelector = "dotnet",
                IssueIdentifier = "1"
            }
        };

        SetupWorkItems(active: active);
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var cut = Render<Work>();

        // The stat strip should contain "oldest:" text from OldestActiveAge()
        // TODO: This only asserts the "oldest:" prefix, not the actual age value. A regression where
        // Ago() returns a wrong format or the wrong item is chosen would still pass this test.
        // Strengthen with Assert.Contains("2h ago", cut.Markup) given DispatchedAt = now.AddHours(-2).
        Assert.Contains("oldest:", cut.Markup);
    }

    [Fact]
    public void WhenActiveListIsEmpty_OldestActiveAge_ShouldShowDash()
    {
        SetupWorkItems(active: []);
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var cut = Render<Work>();

        var statSubs = cut.FindAll(".cockpit-stat-sub");
        // First stat-sub is for "In flight" — should be "—" when empty
        // TODO: The index [0] is positionally fragile — if a stat before "In flight" is added later,
        // this silently tests the wrong element. Prefer targeting the sub within the specific
        // "In flight" stat container using a more scoped locator.
        Assert.True(statSubs.Count >= 1);
        Assert.Equal("—", statSubs[0].TextContent.Trim());
    }

    [Fact]
    public void WhenPendingItemsExist_ShouldShowTypeBreakdownInStatStrip()
    {
        var pending = new List<PendingWorkItemDto>
        {
            new() { Id = Guid.NewGuid(), IssueIdentifier = "1", IssueProviderConfigId = "p", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "a", RetryCount = 0, TimeoutSeconds = 600 },
            new() { Id = Guid.NewGuid(), IssueIdentifier = "2", IssueProviderConfigId = "p", TaskType = WorkItemTaskType.Implementation, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "a", RetryCount = 0, TimeoutSeconds = 600 },
            new() { Id = Guid.NewGuid(), IssueIdentifier = "3", IssueProviderConfigId = "p", TaskType = WorkItemTaskType.Review, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "a", RetryCount = 0, TimeoutSeconds = 600 },
        };

        SetupWorkItems(pending: pending);
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var cut = Render<Work>();

        // TypeBreakdown() should produce something like "impl: 2 · review: 1"
        var statSubs = cut.FindAll(".cockpit-stat-sub");
        Assert.True(statSubs.Count >= 2);
        // TODO: The index [1] assumes "Queued" is the second .cockpit-stat-sub in document order.
        // This is positionally fragile — if DOM order changes or a new stat is inserted before "Queued",
        // this silently tests the wrong element. Prefer a scoped locator targeting the Queued stat container.
        var queueSub = statSubs[1].TextContent.Trim();
        Assert.Contains("impl: 2", queueSub);
        Assert.Contains("review: 1", queueSub);
    }
}
