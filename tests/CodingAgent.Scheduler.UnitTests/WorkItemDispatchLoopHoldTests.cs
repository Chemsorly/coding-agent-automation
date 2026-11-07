using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Spec 056 req 3.5, design DD9 (task 5.3): held work does not keep other work from being dispatched. A hold stops only
/// the job template or credential it names, for the cycle.
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemDispatchLoopHoldTests
{
    private const string Kiro = "dotnet,kiro";
    private const string Claude = "claude";

    private readonly Mock<IPipelineApiWorkItemClient> _client = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly List<Guid> _calls = [];

    public WorkItemDispatchLoopHoldTests()
    {
        _logger.Setup(l => l.ForContext<WorkItemDispatchLoop>()).Returns(_logger.Object);
    }

    private WorkItemDispatchLoop Loop(int rateLimitPerSecond = 100) =>
        new(_client.Object, null, _logger.Object, rateLimitPerSecond: rateLimitPerSecond, interval: TimeSpan.FromMinutes(1));

    private static PendingWorkItemDto Item(string selector = "", string? credential = null) => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = "owner/repo#1",
        IssueProviderConfigId = "github",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow,
        AgentSelector = selector,
        Credential = credential,
        RetryCount = 0,
        TimeoutSeconds = 3600,
    };

    private void Pending(params PendingWorkItemDto[] items) =>
        _client.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(items);

    private void Answers(Func<Guid, DispatchPendingOutcome> answer) =>
        _client.Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) =>
            {
                lock (_calls)
                    _calls.Add(id);
                return answer(id);
            });

    private static DispatchPendingOutcome Held(string reason, string? template = null, string? credential = null) =>
        new(DispatchPendingResult.PermanentRejection, reason, template, credential);

    // req 3.5: 60 Kiro items held for the paused credential, queued ahead of one Claude item. The API returns operator
    // holds last (DD10, PendingQueryOrderTests), so the window starts with the Claude item; the loop dispatches it in the
    // first cycle and, after the first held answer, skips the other Kiro items without calling the API.
    [Fact]
    public async Task SixtyCredentialPausedKiroItems_AheadOfAClaudeItem_TheClaudeItemIsDispatchedInTheFirstCycle()
    {
        var claude = Item();
        var kiroItems = Enumerable.Range(0, 49).Select(_ => Item(Kiro, AgentCredentials.Kiro)).ToList();
        Pending([claude, .. kiroItems]);
        Answers(id => id == claude.Id ? DispatchPendingResult.Dispatched : Held("credential_paused", Kiro, AgentCredentials.Kiro));

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(claude.Id, kiroItems[0].Id);
    }

    // The very first cycle, before any hold is stored: the held items have no stored credential and are each tried; the
    // window (50) then holds them, and the next cycle's order puts them last.
    [Fact]
    public async Task UnresolvedItems_AreEachTried_TheFirstHoldStopsOnlyItemsWithThatStoredCredential()
    {
        var unresolved = Enumerable.Range(0, 3).Select(_ => Item()).ToList();
        Pending([.. unresolved]);
        Answers(_ => Held("credential_paused", Kiro, AgentCredentials.Kiro));

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().HaveCount(3, "an item never resolved is always tried");
    }

    [Fact]
    public async Task AStoppedCredential_SkipsItsLaterItems_ButNotOtherCredentials()
    {
        var first = Item(Kiro, AgentCredentials.Kiro);
        var second = Item(Kiro, AgentCredentials.Kiro);
        var claude = Item(Claude, AgentCredentials.ClaudeSubscription);
        Pending(first, second, claude);
        Answers(id => id == first.Id ? Held("credential_paused", Kiro, AgentCredentials.Kiro) : DispatchPendingResult.Dispatched);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(first.Id, claude.Id);
    }

    // req 3.5 b: a Kiro item at its template limit ahead of a Claude item → both handled in one cycle.
    [Fact]
    public async Task AKiroItemAtItsTemplateLimit_AheadOfAClaudeItem_BothAreHandledInOneCycle()
    {
        var kiro = Item();
        var kiro2 = Item(Kiro, AgentCredentials.Kiro);
        var claude = Item();
        Pending(kiro, kiro2, claude);
        Answers(id => id == kiro.Id ? Held("concurrency_limit", Kiro, AgentCredentials.Kiro) : DispatchPendingResult.Dispatched);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal([kiro.Id, claude.Id], "the second Kiro item is skipped: its stored template is stopped");
    }

    [Fact]
    public async Task PvcUnavailable_StopsOnlyItsTemplate()
    {
        var kiro = Item();
        var kiro2 = Item(Kiro);
        var claude = Item(Claude);
        Pending(kiro, kiro2, claude);
        Answers(id => id == kiro.Id ? Held("pvc_unavailable", Kiro, AgentCredentials.Kiro) : DispatchPendingResult.Dispatched);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(kiro.Id, claude.Id);
    }

    [Theory]
    [InlineData("no_route")]
    [InlineData("profile_unavailable")]
    [InlineData("no_tool")]
    [InlineData("no_template")]
    [InlineData("not_pending")]
    public async Task RoutingHolds_StopNothing(string reason)
    {
        var a = Item(Kiro, AgentCredentials.Kiro);
        var b = Item(Kiro, AgentCredentials.Kiro);
        Pending(a, b);
        Answers(id => id == a.Id ? Held(reason, Kiro, AgentCredentials.Kiro) : DispatchPendingResult.Dispatched);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(a.Id, b.Id);
    }

    // DD9: 30 quick holds, then a Claude item, with the real rate limiter (10 per second): the calls wait for their
    // token instead of aborting the cycle, so the Claude item is dispatched within the first cycle.
    [Fact]
    public async Task ThirtyUnresolvedItems_ThenAClaudeItem_WithTheRealLimiter_TheClaudeItemIsDispatchedInTheFirstCycle()
    {
        var held = Enumerable.Range(0, 30).Select(_ => Item()).ToList();
        var claude = Item();
        Pending([.. held, claude]);
        var heldIds = held.Select(i => i.Id).ToHashSet();
        Answers(id => heldIds.Contains(id) ? Held("no_route") : DispatchPendingResult.Dispatched);

        await Loop(rateLimitPerSecond: 10).PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().HaveCount(31).And.EndWith(claude.Id);
    }

    [Fact]
    public async Task GlobalLimit_EndsTheCycle()
    {
        var a = Item();
        var b = Item();
        Pending(a, b);
        Answers(_ => new DispatchPendingOutcome(DispatchPendingResult.GlobalLimitReached, "global_concurrency_limit"));

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(a.Id);
    }

    [Fact]
    public async Task A503_AbortsTheCycle()
    {
        var a = Item();
        var b = Item();
        Pending(a, b);
        Answers(_ => DispatchPendingResult.Transient);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(a.Id);
    }

    // Rollout window (design "Rollout Window"): an API of an earlier release names no template; the loop stops the
    // item's stored selector.
    [Fact]
    public async Task AnAnswerWithoutTemplate_StopsTheStoredSelector()
    {
        var a = Item(Kiro);
        var b = Item(Kiro);
        var c = Item(Claude);
        Pending(a, b, c);
        Answers(id => id == a.Id ? Held("concurrency_limit") : DispatchPendingResult.Dispatched);

        await Loop().PollAndDispatchAsync(CancellationToken.None);

        _calls.Should().Equal(a.Id, c.Id);
    }
}
