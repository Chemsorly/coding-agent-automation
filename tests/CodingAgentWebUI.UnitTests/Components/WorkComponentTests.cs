using System.Reflection;
using AwesomeAssertions;
using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="Work"/> covering the Priority column
/// added by issue #2360: rendering, @onchange callback wiring, out-of-range guard,
/// error display, and revert-via-refresh on failure.
/// </summary>
public class WorkComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItems = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();

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
        Services.AddSingleton(Mock.Of<IProviderFactory>());
        Services.AddSingleton(Mock.Of<IDependencyChecker>());
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

    // ── Test 8: In-flight table renders issue number + title ─────────────────

    /// <summary>
    /// Builds a minimal <see cref="ActiveWorkItemDto"/> for in-flight table rendering tests.
    /// </summary>
    private static ActiveWorkItemDto MakeActiveItem(Guid id, string issueIdentifier = "2231", string? issueTitle = null) => new()
    {
        Id = id,
        Status = WorkItemStatus.Running,
        DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        AgentSelector = "kiro",
        IssueIdentifier = issueIdentifier,
        IssueTitle = issueTitle
    };

    [Fact]
    public void InFlightTable_RendersIssueNumber_AndTitle()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(itemId, "2231", "Fix critical regression in scheduler")]);

        var cut = Render<Work>();

        // The in-flight table must show both the issue number and the title.
        var markup = cut.Markup;
        markup.Should().Contain("#2231", "the issue number must appear in the in-flight row");
        markup.Should().Contain("Fix critical regression in scheduler",
            "the issue title must appear in the in-flight row when IssueTitle is populated");
    }

    [Fact]
    public void InFlightTable_RendersIssueNumber_WhenTitleIsNull()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(itemId, "2231", issueTitle: null)]);

        var cut = Render<Work>();

        // The row must still render even when IssueTitle is null (legacy/minimal payload path).
        cut.Markup.Should().Contain("#2231", "the issue number must appear even when IssueTitle is null");
    }

    // ── Test 9: Clicking an in-flight row navigates to the run detail page ───

    [Fact]
    public async Task InFlightTable_RowClick_NavigatesToRunDetail()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(itemId, "2231", "Fix regression")]);

        var cut = Render<Work>();
        var nav = Services.GetRequiredService<NavigationManager>();

        // Click the in-flight row (the <tr> with monitoring-row-clickable).
        await cut.InvokeAsync(() =>
        {
            var row = cut.Find(".monitoring-table tbody tr.monitoring-row-clickable");
            row.Click();
        });

        nav.Uri.Should().EndWith($"/runs/{itemId}",
            "clicking an in-flight row must navigate to the run detail page for that work item's id");
    }

    // ── Test 10: Cancel button click does NOT navigate (stopPropagation) ─────
    // TODO [WARNING]: This test does not actually verify that @onclick:stopPropagation="true" is working.
    // In bUnit, clicking a child <button> directly does not simulate DOM event bubbling up through the <td>
    // that carries the directive — the click never reaches the <tr> handler regardless of whether the
    // directive is present or absent. The assertion nav.Uri.Should().Be(initialUri) would pass even if
    // @onclick:stopPropagation were removed from the markup. A true regression would not be caught here.
    // Consider an integration/E2E test or a DOM-bubbling simulation to properly cover this behaviour.

    [Fact]
    public async Task InFlightTable_CancelButtonClick_DoesNotNavigate()
    {
        var itemId = Guid.NewGuid();
        _mockWorkItems
            .Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeActiveItem(itemId, "2231", "Fix regression")]);
        _mockWorkItems
            .Setup(c => c.PostStatusAsync(It.IsAny<Guid>(), It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<Work>();
        var nav = Services.GetRequiredService<NavigationManager>();
        var initialUri = nav.Uri;

        // Click only the Cancel button — propagation is stopped, so nav should not change.
        await cut.InvokeAsync(async () =>
        {
            var cancelBtn = cut.Find(".btn-cancel-small");
            await cancelBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        });

        nav.Uri.Should().Be(initialUri,
            "clicking the Cancel button must not trigger row navigation (stopPropagation is set on the button cell)");
    }
}
