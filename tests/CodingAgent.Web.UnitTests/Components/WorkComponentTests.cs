using System.Reflection;
using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="Work"/> covering the Priority column
/// added by issue #2360: rendering, @onchange callback wiring, out-of-range guard,
/// error display, and revert-via-refresh on failure.
/// </summary>
public class WorkComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItems = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IDependencyChecker> _mockDependencyChecker = new();

    /// <summary>
    /// Builds a minimal <see cref="PendingWorkItemDto"/> suitable for queue-table rendering tests.
    /// </summary>
    private static PendingWorkItemDto MakePendingItem(Guid id, int priorityWeight = 0) => new()
    {
        Id = id,
        IssueIdentifier = "42",
        IssueProviderConfigId = "github",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        AgentSelector = "kiro",
        RetryCount = 0,
        TimeoutSeconds = 3600,
        PriorityWeight = priorityWeight,
    };

    public WorkComponentTests()
    {
        // IPipelineApiWorkItemClient — mocked; default stubs return empty lists.
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // IPipelineApiConfigClient — needed by BlockedIssuesService (sealed, no interface).
        // Returning empty templates makes GetBacklogAsync a no-op, keeping test setup minimal.
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        Services.AddSingleton<IPipelineApiWorkItemClient>(_mockWorkItems.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);
        Services.AddSingleton(_mockProviderFactory.Object);
        Services.AddSingleton(_mockDependencyChecker.Object);
        // BlockedIssuesService is sealed — let DI construct the real instance from the mocks above.
        Services.AddSingleton<BlockedIssuesService>();
        Services.AddSingleton(new CockpitState());
        // NavigationManager is provided automatically by bunit.
    }

    // ── Test 1: Priority column header ────────────────────────────────────────

    [Fact]
    public void WorkQueueTable_HasPriorityColumnHeader()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId)]);

        var cut = Render<Work>();

        // The queue table <thead> must contain a "Priority" column header.
        var headers = cut.FindAll(".monitoring-table thead th");
        headers.Should().Contain(h => h.TextContent == "Priority",
            "the Priority column header must be present in the queue table");
    }

    // ── Test 2: Priority weight rendered in input ─────────────────────────────

    [Fact]
    public void WorkQueueTable_RendersPriorityWeight_ForPendingRow()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId, priorityWeight: 450)]);

        var cut = Render<Work>();

        // TODO: This asserts only on the static HTML attribute at initial render time. Blazor's one-way
        // value= binding sets the attribute at render but does not guarantee it updates after a
        // subsequent RefreshQuietAsync call. Add a follow-up test that triggers a successful priority
        // update and then asserts the input attribute reflects the new server-returned value, covering
        // AC "on successful update the displayed value reflects the new weight". (#2360 warning)
        var input = cut.Find("input[type=number]");
        input.GetAttribute("value").Should().Be("450", "the input must reflect PriorityWeight from the DTO");
        input.GetAttribute("min").Should().Be("0");
        input.GetAttribute("max").Should().Be("1000");
    }

    // ── Test 3: @onchange fires SetPriorityAsync ──────────────────────────────

    [Fact]
    public async Task WorkQueueTable_Onchange_CallsSetPriorityAsync()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId)]);
        _mockWorkItems
            .Setup(c => c.SetPriorityAsync(itemId, 750, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<Work>();

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new ChangeEventArgs { Value = "750" });
        });

        _mockWorkItems.Verify(c => c.SetPriorityAsync(itemId, 750, It.IsAny<CancellationToken>()), Times.Once);
        // TODO: This only verifies the API was called — it does not assert that the displayed value
        // (input attribute or _pending state) was subsequently updated after RefreshQuietAsync ran.
        // AC states "on successful update the displayed value reflects the new weight without a full
        // page reload." Add an assertion on the re-rendered input value to catch a regression where
        // RefreshQuietAsync is accidentally removed from the success path. (#2360 warning)
    }

    // ── Test 4: Out-of-range value is silently ignored ────────────────────────

    // TODO: This test only covers the upper boundary (1001). The @onchange guard also rejects negative
    // values (w >= 0 check). Add tests for -1 (negative boundary) and non-numeric input (e.g. "abc",
    // which takes the int.TryParse failure branch). Also add positive-boundary tests asserting that the
    // values 0 and 1000 ARE forwarded to the API — without them, a regression shifting the limit to
    // > 999 would go undetected. (#2360 warning)
    [Fact]
    public async Task WorkQueueTable_OnSetPriority_OutOfRange_DoesNotCallSetPriorityAsync()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId)]);

        var cut = Render<Work>();

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new ChangeEventArgs { Value = "1001" });
        });

        _mockWorkItems.Verify(
            c => c.SetPriorityAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a value outside 0–1000 must not be forwarded to the API");
    }

    // ── Test 5: Failed update shows error message ─────────────────────────────

    [Fact]
    public async Task WorkQueueTable_OnSetPriorityFailure_ShowsErrorMessage()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId)]);
        _mockWorkItems
            .Setup(c => c.SetPriorityAsync(itemId, 100, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("400 Bad Request"));

        var cut = Render<Work>();

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new ChangeEventArgs { Value = "100" });
        });

        cut.Markup.Should().Contain("Priority update failed",
            "a failed priority update must surface an error message in the UI");
    }

    // ── Test 6: Failed update triggers a refresh (revert mechanism) ───────────

    [Fact]
    public async Task WorkQueueTable_OnSetPriorityFailure_TriggersRefresh()
    {
        var itemId = Guid.NewGuid();
        var pendingCallCount = 0;
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pendingCallCount++;
                return [MakePendingItem(itemId)];
            });
        _mockWorkItems
            .Setup(c => c.SetPriorityAsync(itemId, 200, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("409 Conflict"));

        var cut = Render<Work>();

        // OnInitializedAsync makes the first call; reset counter so we only count post-init calls.
        // TODO: callsAfterInit is captured after Render<Work>() returns, but OnInitializedAsync and
        // OnAfterRenderAsync are async — additional calls from LoadBacklogAsync → RefreshQuietAsync
        // may fire between the capture and the change event, making the baseline unreliable. A stronger
        // approach is to verify the mock was called at least once *after* the change event using
        // Moq's Verify with a sequence/counter snapshot rather than a shared mutable counter.
        // The current assertion (> callsAfterInit) can pass incidentally if auto-refresh fires. (#2360 warning)
        var callsAfterInit = pendingCallCount;

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new ChangeEventArgs { Value = "200" });
        });

        pendingCallCount.Should().BeGreaterThan(callsAfterInit,
            "a failed priority update must call GetPendingAsync again to revert the DOM input");
    }

    // ── Test 7: Successful update clears a stale error ────────────────────────

    [Fact]
    public async Task WorkQueueTable_OnSetPrioritySuccess_ClearsPriorityError()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakePendingItem(itemId)]);
        _mockWorkItems
            .Setup(c => c.SetPriorityAsync(itemId, 300, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<Work>();

        // Seed a stale error via reflection so the error div is visible before the successful update.
        // TODO: Using reflection to access _priorityError couples this test to the private field name.
        // If the field is renamed, field!.SetValue throws NullReferenceException rather than a clear
        // assertion failure. A more resilient approach is to trigger an actual API failure first (via
        // a prior ChangeAsync with a mocked exception), then trigger the success — this exercises the
        // real fail→succeed sequence and avoids reflection entirely. (#2360 warning)
        var field = cut.Instance.GetType()
            .GetField("_priorityError", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(cut.Instance, "stale error");
        // Force a re-render so the seeded error div appears in the markup.
        cut.Render();

        cut.Markup.Should().Contain("stale error", "pre-condition: error div must be visible before the test acts");

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new ChangeEventArgs { Value = "300" });
        });

        cut.Markup.Should().NotContain("stale error",
            "a successful priority update must clear any previously displayed error");
    }

    // ── Helper: active work item ──────────────────────────────────────────────

    private static ActiveWorkItemDto MakeActiveItem(Guid id, string issueIdentifier = "42", string? issueTitle = null) => new()
    {
        Id = id,
        IssueIdentifier = issueIdentifier,
        Status = WorkItemStatus.Running,
        DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        AgentSelector = "kiro",
        TimeoutSeconds = 3600,
        IssueTitle = issueTitle
    };

    // ── In-flight title display (issue #2335) ─────────────────────────────────

    [Fact]
    public void InFlightTable_DisplaysIssueTitleAlongsideNumber_WhenTitleIsPresent()
    {
        var id = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(id, "2231", "Add Grafana traces panel")]);

        var cut = Render<Work>();

        // Both the issue number and the title must appear in the in-flight table row.
        var markup = cut.Markup;
        markup.Should().Contain("#2231",
            "the issue number must be rendered in the in-flight row");
        markup.Should().Contain("Add Grafana traces panel",
            "the issue title must be rendered alongside the number in the in-flight row");
    }

    [Fact]
    public void InFlightTable_DoesNotShowTitleSpan_WhenTitleIsNull()
    {
        var id = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(id, "2231", issueTitle: null)]);

        var cut = Render<Work>();

        // The number must appear; no extra title span content should cause a rendering error.
        cut.Markup.Should().Contain("#2231",
            "the issue number must still be rendered when IssueTitle is null");
        // Confirm the row renders without throwing.
        cut.FindAll(".monitoring-table tbody tr").Should().HaveCount(1,
            "exactly one in-flight row must render");
        // TODO: [WARNING] This test does not assert that the title span is absent when IssueTitle is null.
        // If the @if (!string.IsNullOrEmpty(a.IssueTitle)) guard were removed, this test would still pass.
        // Add: cut.Markup.Should().NotContain("text-muted", ...) or a more targeted assertion that verifies
        // no title <span> is emitted, to lock in the conditional rendering behaviour.
    }

    // ── In-flight click navigation (issue #2335) ──────────────────────────────

    [Fact]
    public void InFlightRow_RowHasClickableClass()
    {
        var id = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(id, "2231", "Some issue")]);

        var cut = Render<Work>();

        // TODO [WARNING]: This test only verifies that the CSS class monitoring-row-clickable is present
        // on the row. It does not assert that the row has a bound @onclick handler. A class can be present
        // without any click event, so this test does not verify the navigation behaviour it implies.
        // The adjacent InFlightRow_Click_NavigatesToRunDetailPage test provides meaningful coverage, but
        // this test should be strengthened or removed to avoid redundancy.
        // TODO [WARNING]: The selector ".monitoring-table tbody tr.monitoring-row-clickable" may match rows
        // from tables other than the in-flight table if the Work component renders multiple tables with
        // matching structure. Scope the search to the in-flight section specifically (e.g. by finding the
        // in-flight table by its heading or a stable data-testid attribute) to avoid count-sensitivity to
        // unrelated table content.
        var rows = cut.FindAll(".monitoring-table tbody tr.monitoring-row-clickable");
        rows.Should().HaveCount(1,
            "in-flight rows must carry the monitoring-row-clickable CSS class for cursor and clickability");
    }

    [Fact]
    public void InFlightRow_Click_NavigatesToRunDetailPage()
    {
        var id = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(id, "2231", "Some issue")]);

        var navMan = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Work>();

        var row = cut.Find(".monitoring-table tbody tr.monitoring-row-clickable");
        row.Click();

        // WorkItem.Id == run id; NavigateTo produces a full URI based on the base URI.
        navMan.Uri.Should().EndWith($"runs/{id}",
            "clicking an in-flight row must navigate to the run detail page at runs/{id}");
    }

    [Fact]
    public void InFlightRow_CancelButton_DoesNotNavigate()
    {
        var id = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(id, "2231", "Some issue")]);
        _mockWorkItems
            .Setup(c => c.PostStatusAsync(id, It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var navMan = Services.GetRequiredService<NavigationManager>();
        var initialUri = navMan.Uri;
        var cut = Render<Work>();

        var cancelBtn = cut.Find(".btn-cancel-small");
        cancelBtn.Click();

        // Clicking Cancel must NOT trigger navigation — stopPropagation prevents the row click.
        navMan.Uri.Should().Be(initialUri,
            "clicking the Cancel button must not propagate to the row @onclick and must not navigate");
    }

    // TODO: [WARNING] Acceptance criterion "Clicking an active run row on the Overview page opens the run
    // detail / pipeline sidebar" has no bunit component test. The Overview.razor active-run click path
    // (cockpit-run-row → OpenRun(run.RunId)) is covered only by a pre-existing E2E test
    // (ActiveRun_RowClick_OpensRunDetailPage in MonitoringInteractionTests.cs). Add a bunit test for
    // Overview.razor that: (1) mocks IPipelineApiRunHistoryClient to return an active run, (2) renders
    // Overview, (3) clicks the cockpit-run-row element, and (4) asserts NavigationManager.Uri ends with
    // "runs/{runId}" — matching the pattern of InFlightRow_Click_NavigatesToRunDetailPage above.

    // ── Provider backlog card (issue #2487) ───────────────────────────────────

    /// <summary>
    /// Sets up config+provider+dep mocks so GetBacklogAsync returns the given issues
    /// with HasMore=false (not truncated).
    /// </summary>
    private void SetupBacklogProvider(IReadOnlyList<IssueSummary> issues)
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "T",
            IssueProviderId = "prov1",
            RepoProviderId = "repo1",
            Enabled = true
        };
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var pagedResult = new PagedResult<IssueSummary>
        {
            Items = issues,
            Page = 1,
            PageSize = 50,
            HasMore = false
        };
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedResult);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);
    }

    [Fact]
    public async Task BacklogCard_ShowsExactCount_WhenNotTruncated()
    {
        // 2 ready issues, 1 blocked — provider returns HasMore=false so IsTruncated=false.
        SetupBacklogProvider(new[]
        {
            new IssueSummary { Identifier = "10", Title = "Ready one", Labels = Array.Empty<string>(), Description = "", Url = null },
            new IssueSummary { Identifier = "11", Title = "Ready two", Labels = Array.Empty<string>(), Description = "", Url = null },
        });

        var cut = Render<Work>();

        // Wait for the backlog to finish loading (OnAfterRenderAsync sets _backlogLoading=false).
        await cut.WaitForStateAsync(
            () => !cut.Markup.Contains("checking…"),
            TimeSpan.FromSeconds(5));

        // The backlog card is the last .cockpit-card on the page. The header span shows the count.
        // TODO: [WARNING] Using .Last() on .cockpit-card-header span is fragile: if a new cockpit-card
        // is added after the backlog card, .Last() will target the wrong span and the assertion may
        // pass on unrelated content while the actual backlog header is never checked. Consider finding
        // the cockpit-card that contains "Provider backlog" text and then locating its span within.
        var headerSpan = cut.FindAll(".cockpit-card-header span").Last();
        // Header should show exact "N ready · M open" without a "+" suffix.
        headerSpan.TextContent.Should().MatchRegex(@"\d+ ready · \d+ open$",
            "non-truncated header must show exact counts without a + suffix");
        headerSpan.TextContent.Should().NotContain("+",
            "non-truncated header must not contain a + indicator");
    }

    [Fact]
    public async Task BacklogCard_ShowsTruncationIndicator_WhenResultIsTruncated()
    {
        // Set up provider to always return HasMore=true (service hits MaxIssuesPerProvider cap).
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "T",
            IssueProviderId = "prov1",
            RepoProviderId = "repo1",
            Enabled = true
        };
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        // Derive page content from the 'page' argument so both overloads produce the same
        // deterministic result without a shared mutable counter that could be double-incremented
        // when both the 3-arg and 4-arg Moq setups are live simultaneously.
        PagedResult<IssueSummary> MakeInfinitePage(int pageArg) => new PagedResult<IssueSummary>
        {
            Items = Enumerable.Range((pageArg - 1) * 50 + 1, 50)
                .Select(i => new IssueSummary { Identifier = i.ToString(), Title = $"I{i}", Labels = Array.Empty<string>(), Description = "", Url = null })
                .ToArray(),
            Page = pageArg,
            PageSize = 50,
            HasMore = true
        };

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, CancellationToken _) => MakeInfinitePage(pg));
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, IReadOnlyList<string>? _, CancellationToken _) => MakeInfinitePage(pg));

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockProvider.Object);

        _mockDependencyChecker
            .Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var cut = Render<Work>();

        // Wait for the backlog to finish loading (OnAfterRenderAsync sets _backlogLoading=false).
        await cut.WaitForStateAsync(
            () => !cut.Markup.Contains("checking…"),
            TimeSpan.FromSeconds(30)); // 200 dep-check calls are mocked but may take time

        var headerSpan = cut.FindAll(".cockpit-card-header span").Last();
        // Header should contain a "+" to indicate truncation.
        headerSpan.TextContent.Should().Contain("+",
            "truncated header must contain a + indicator when IsTruncated is true");
        // The table must still render the fetched issues.
        var rows = cut.FindAll(".monitoring-table tbody tr");
        rows.Should().HaveCountGreaterThanOrEqualTo(1, "truncated backlog must still render fetched issues");
    }

    [Fact]
    public async Task BacklogCard_ShowsEmptyMessage_WhenNoIssuesFound()
    {
        // Default config setup: empty templates → GetBacklogAsync returns empty Issues, IsTruncated=false.
        // The constructor already stubs GetAllTemplatesAsync with empty templates; this confirms the
        // empty-state div is shown.
        var cut = Render<Work>();

        // Wait for the backlog to finish loading.
        await cut.WaitForStateAsync(
            () => !cut.Markup.Contains("checking…"),
            TimeSpan.FromSeconds(5));

        cut.Markup.Should().Contain("No open issues found",
            "the empty-state message must be shown when no issues are returned");
    }
}
