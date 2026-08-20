using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for the two dispatch-deduplication endpoints:
/// <c>GET /api/work-items/is-distributed</c> and <c>GET /api/work-items/active-identifiers</c>.
///
/// These are the authoritative implementation of the dedup predicate — KubernetesWorkDistributor
/// is a pure Pipeline API client and delegates IsIssueDistributedAsync /
/// GetActiveIssueIdentifiersAsync straight through to them. The predicate they encode is:
///
///   distributed = Status in {Pending, Dispatched, Running}
///                 OR (Status terminal AND CompletedAt within DefaultRestartDedupCooldown)
///
/// The "true iff active" half mirrors IsIssueDistributedConsistencyPropertyTests
/// (tests/CodingAgentWebUI.Infrastructure.UnitTests/Persistence), which asserts the same
/// invariant against the DB-backed copy in DbWorkDistributorBase.
///
/// Driven over HTTP: the handlers are internal to CodingAgentWebUI.Api, which grants no
/// InternalsVisibleTo to this assembly, and going through the pipeline also covers route
/// registration, query binding, and response serialization the way the client consumes it.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class DispatchDedupEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    /// <summary>The recently-terminated window the endpoints measure CompletedAt against.</summary>
    private static readonly TimeSpan Cooldown = PipelineConstants.DefaultRestartDedupCooldown;

    /// <summary>
    /// Distance from the cooldown edge used by the boundary tests. The endpoint derives its cutoff
    /// from its own UtcNow, always slightly later than the UtcNow used to seed CompletedAt, so an
    /// exact-edge timestamp would land arbitrarily on either side. 30s absorbs that drift while
    /// staying small relative to the 5-minute window.
    /// </summary>
    private static readonly TimeSpan BoundaryMargin = TimeSpan.FromSeconds(30);

    public DispatchDedupEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    // The factory is a collection fixture, so the InMemory database is shared with every other
    // test class in the collection. Each test invents its own identifiers and asserts only about
    // those, which keeps the global active-identifiers query free of cross-test interference.
    private static string NewIssueId() => $"owner/repo#{Guid.NewGuid():N}";
    private static string NewProviderId() => $"provider-{Guid.NewGuid():N}";

    private void Seed(string issueIdentifier, string issueProviderConfigId, WorkItemStatus status,
        DateTimeOffset? completedAt = null)
    {
        using var db = _factory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            Status = status,
            Payload = null,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt
        });
        db.SaveChanges();
    }

    private async Task<bool> IsDistributedAsync(string issueIdentifier, string issueProviderConfigId)
    {
        var url = $"/api/work-items/is-distributed?issueIdentifier={Uri.EscapeDataString(issueIdentifier)}"
                + $"&issueProviderConfigId={Uri.EscapeDataString(issueProviderConfigId)}";
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        return body.GetProperty("isDistributed").GetBoolean();
    }

    private async Task<List<ActiveIdentifierDto>> GetActiveIdentifiersAsync()
    {
        var response = await _client.GetAsync("/api/work-items/active-identifiers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<ActiveIdentifierDto>>(PipelineJsonOptions.Default);
        result.Should().NotBeNull();
        return result!;
    }

    private static int CountOf(IEnumerable<ActiveIdentifierDto> identifiers, string issueIdentifier,
        string issueProviderConfigId)
        => identifiers.Count(i => i.IssueIdentifier == issueIdentifier
                               && i.IssueProviderConfigId == issueProviderConfigId);

    /// <summary>Mirrors the client-side shape in PipelineApiWorkItemClient.ActiveIdentifierDto.</summary>
    private sealed record ActiveIdentifierDto
    {
        public string IssueIdentifier { get; init; } = "";
        public string IssueProviderConfigId { get; init; } = "";
    }

    // ── is-distributed: status predicate ─────────────────────────────────────────

    /// <summary>
    /// With CompletedAt unset the recently-terminated branch cannot fire, so the answer is exactly
    /// "status is active". Same invariant as IsIssueDistributedConsistencyPropertyTests, pinned
    /// here against the endpoint over all six WorkItemStatus values.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Dispatched, true)]
    [InlineData(WorkItemStatus.Running, true)]
    [InlineData(WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Failed, false)]
    [InlineData(WorkItemStatus.Cancelled, false)]
    public async Task GetIsDistributed_ReturnsTrue_IffStatusIsActive(WorkItemStatus status, bool expected)
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, status);

        var isDistributed = await IsDistributedAsync(issueId, providerId);

        isDistributed.Should().Be(expected);
    }

    [Fact]
    public async Task GetIsDistributed_ReturnsFalse_WhenNoWorkItemExists()
    {
        var isDistributed = await IsDistributedAsync(NewIssueId(), NewProviderId());

        isDistributed.Should().BeFalse();
    }

    /// <summary>
    /// The predicate keys on the (issueIdentifier, issueProviderConfigId) pair, not the issue alone:
    /// the same issue number under a different provider config is a different unit of work.
    /// </summary>
    [Fact]
    public async Task GetIsDistributed_ReturnsFalse_WhenIssueMatchesButProviderConfigDiffers()
    {
        var issueId = NewIssueId();
        Seed(issueId, NewProviderId(), WorkItemStatus.Running);

        var isDistributed = await IsDistributedAsync(issueId, NewProviderId());

        isDistributed.Should().BeFalse();
    }

    // ── is-distributed: recently-terminated cooldown ─────────────────────────────

    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task GetIsDistributed_ReturnsTrue_WhenTerminalCompletedInsideCooldown(WorkItemStatus status)
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, status, DateTimeOffset.UtcNow - (Cooldown - BoundaryMargin));

        var isDistributed = await IsDistributedAsync(issueId, providerId);

        isDistributed.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task GetIsDistributed_ReturnsFalse_WhenTerminalCompletedOutsideCooldown(WorkItemStatus status)
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, status, DateTimeOffset.UtcNow - (Cooldown + BoundaryMargin));

        var isDistributed = await IsDistributedAsync(issueId, providerId);

        isDistributed.Should().BeFalse();
    }

    /// <summary>
    /// The real dispatch scenario: an issue accumulates several WorkItems over time. One active row
    /// makes the pair distributed regardless of how stale its terminal siblings are.
    /// </summary>
    [Fact]
    public async Task GetIsDistributed_ReturnsTrue_WhenAnyRowIsActive_DespiteStaleTerminalSiblings()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Failed, DateTimeOffset.UtcNow - (Cooldown + BoundaryMargin));
        Seed(issueId, providerId, WorkItemStatus.Cancelled, DateTimeOffset.UtcNow - (Cooldown + BoundaryMargin));
        Seed(issueId, providerId, WorkItemStatus.Pending);

        var isDistributed = await IsDistributedAsync(issueId, providerId);

        isDistributed.Should().BeTrue();
    }

    // ── active-identifiers: status predicate ─────────────────────────────────────

    /// <summary>
    /// The set half of the same predicate: with CompletedAt unset, a pair is listed iff its status
    /// is active.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Dispatched, true)]
    [InlineData(WorkItemStatus.Running, true)]
    [InlineData(WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Failed, false)]
    [InlineData(WorkItemStatus.Cancelled, false)]
    public async Task GetActiveIdentifiers_ContainsPair_IffStatusIsActive(WorkItemStatus status, bool expected)
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, status);

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(expected ? 1 : 0);
    }

    // ── active-identifiers: recently-terminated cooldown ─────────────────────────

    [Fact]
    public async Task GetActiveIdentifiers_ContainsPair_WhenTerminalCompletedInsideCooldown()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Succeeded, DateTimeOffset.UtcNow - (Cooldown - BoundaryMargin));

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(1);
    }

    [Fact]
    public async Task GetActiveIdentifiers_OmitsPair_WhenTerminalCompletedOutsideCooldown()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Succeeded, DateTimeOffset.UtcNow - (Cooldown + BoundaryMargin));

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(0);
    }

    /// <summary>
    /// A terminal row with no CompletedAt at all must not slip into the recently-terminated branch —
    /// the endpoint requires CompletedAt != null before comparing against the cutoff.
    /// </summary>
    [Fact]
    public async Task GetActiveIdentifiers_OmitsPair_WhenTerminalWithNoCompletedAt()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Failed, completedAt: null);

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(0);
    }

    // ── active-identifiers: de-duplication ───────────────────────────────────────

    /// <summary>
    /// Several active WorkItems for one issue collapse to a single entry — the caller treats the
    /// result as a set of already-dispatched issues, so repeats would inflate it pointlessly.
    /// </summary>
    [Fact]
    public async Task GetActiveIdentifiers_DeduplicatesRepeatedPairs()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Pending);
        Seed(issueId, providerId, WorkItemStatus.Dispatched);
        Seed(issueId, providerId, WorkItemStatus.Running);

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(1);
    }

    /// <summary>
    /// De-duplication also spans the union: a pair that is both active and recently terminated is
    /// picked up by each of the two queries, and must still be listed once.
    /// </summary>
    [Fact]
    public async Task GetActiveIdentifiers_DeduplicatesAcrossActiveAndRecentlyTerminated()
    {
        var issueId = NewIssueId();
        var providerId = NewProviderId();
        Seed(issueId, providerId, WorkItemStatus.Running);
        Seed(issueId, providerId, WorkItemStatus.Failed, DateTimeOffset.UtcNow - (Cooldown - BoundaryMargin));

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerId).Should().Be(1);
    }

    /// <summary>
    /// De-duplication is per pair, not per issue: the same issue identifier under two provider
    /// configs stays two distinct entries.
    /// </summary>
    [Fact]
    public async Task GetActiveIdentifiers_KeepsSameIssueUnderDifferentProviderConfigsSeparate()
    {
        var issueId = NewIssueId();
        var providerA = NewProviderId();
        var providerB = NewProviderId();
        Seed(issueId, providerA, WorkItemStatus.Running);
        Seed(issueId, providerB, WorkItemStatus.Running);

        var identifiers = await GetActiveIdentifiersAsync();

        CountOf(identifiers, issueId, providerA).Should().Be(1);
        CountOf(identifiers, issueId, providerB).Should().Be(1);
    }

    // ── cross-endpoint consistency ───────────────────────────────────────────────

    /// <summary>
    /// The two endpoints answer the same question about the same pair — one issue at a time, or a
    /// whole batch — and the dispatch loop uses both. They are separate handlers that each rebuild
    /// the predicate from the shared status set and their own cutoff, so nothing else stops one
    /// from being edited without the other, and a disagreement means duplicate dispatch.
    ///
    /// Sweeps all six statuses against CompletedAt unset / inside the cooldown / outside it, which
    /// covers every branch either handler can take. (An active row carrying a CompletedAt is not a
    /// real state, but it is a cheap way to check both handlers short-circuit on "active" alike.)
    /// </summary>
    [Fact]
    public async Task BothEndpoints_AgreeOnEveryPair_AcrossMixedStatusesAndCooldownPositions()
    {
        var insideCooldown = DateTimeOffset.UtcNow - (Cooldown - BoundaryMargin);
        var outsideCooldown = DateTimeOffset.UtcNow - (Cooldown + BoundaryMargin);
        var completedAtCases = new DateTimeOffset?[] { null, insideCooldown, outsideCooldown };

        var statuses = new[]
        {
            WorkItemStatus.Pending, WorkItemStatus.Dispatched, WorkItemStatus.Running,
            WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled
        };

        var pairs = new List<(string IssueId, string ProviderId)>();
        foreach (var status in statuses)
        {
            foreach (var completedAt in completedAtCases)
            {
                var issueId = NewIssueId();
                var providerId = NewProviderId();
                Seed(issueId, providerId, status, completedAt);
                pairs.Add((issueId, providerId));
            }
        }

        var identifiers = await GetActiveIdentifiersAsync();

        foreach (var (issueId, providerId) in pairs)
        {
            var isDistributed = await IsDistributedAsync(issueId, providerId);
            var listed = CountOf(identifiers, issueId, providerId) > 0;

            isDistributed.Should().Be(listed,
                "is-distributed and active-identifiers must agree about ({0}, {1})", issueId, providerId);
        }
    }
}
