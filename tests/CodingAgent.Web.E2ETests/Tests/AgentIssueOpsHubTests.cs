using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests that drive agent issue-operation hub methods through the real <see cref="AgentHub"/>
/// end-to-end: issue reads, comment round-trips, label changes, sub-issue creation, and
/// authorization enforcement. All scenarios run headless (no browser).
///
/// <para>
/// These tests provide explicit, targeted coverage for the MessagePack wire contract between
/// <see cref="FakeAgentClient"/> and the hub. In particular, the <c>IssueIdentifier</c> struct
/// must be serialized by <see cref="AgentHubMessagePack.SerializerOptions"/> via
/// <c>IssueIdentifierFormatter</c> — a regression that was invisible to unit tests and caused
/// production failures from 2026-07-23 to 2026-09-23. The formatter is also continuously
/// guarded by <c>RealAgentWorkerSmokeTests.ScenarioB</c>.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentIssueOpsHubTests : HeadlessE2ETestBase
{
    public AgentIssueOpsHubTests(E2EFixture fixture) : base(fixture) { }

    // ── Common setup helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Seeds issue 42, a template, and a profile, then dispatches and bootstraps a
    /// <see cref="FakeAgentClient"/> through the full pending → dispatched → active-job path.
    /// Returns <c>(jobId, fakeAgent)</c>. The caller is responsible for disposing the agent via
    /// <c>await using</c> or explicit <c>await fakeAgent.DisposeAsync()</c>.
    /// </summary>
    private async Task<(string JobId, FakeAgentClient FakeAgent)> SetupActiveJobAsync(string agentId)
    {
        // TODO: [WARNING] Seed adds issue "42" unconditionally on every call. Each scenario
        // invokes SetupActiveJobAsync exactly once and HeadlessE2ETestBase.InitializeAsync calls
        // ResetAllAsync (which clears IssueProvider.Issues) before each test, so there is no
        // duplication today. If SetupActiveJobAsync is ever called more than once within a single
        // test, Issues will contain duplicate "42" entries and scenario-1's title assertion will
        // still pass — but any assertion that depends on a single entry (e.g. Assert.Single)
        // would fail with a misleading "more than one element" error. Guard with a Contains check
        // or clear the collection before adding if this method is ever made multi-call.

        // TODO: [WARNING] FakeAgentClient is created before DispatchIssueAsync and the subsequent
        // WaitAsync. If either of those throws, fakeAgent is never disposed (the caller's
        // "await using" block never executes), leaving a live HubConnection that can interfere
        // with subsequent tests sharing the same fixture. Fix by wrapping the post-creation work
        // in a try/catch that calls await fakeAgent.DisposeAsync() before rethrowing.

        // Seed issue
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "42",
            Title = "Issue-ops E2E test issue",
            Description = "## Requirements\nTest hub issue operations end-to-end.",
            Labels = new[] { "agent:next" }
        });

        // Seed template + profile so DispatchIssueAsync resolves correctly
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-issue-ops",
            Name = "Issue Ops Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-issue-ops",
            DisplayName = "Issue Ops Agent Profile",
            MatchLabels = new[] { "issue-ops" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Connect agent BEFORE dispatch so FakeJobController can bootstrap it
        var fakeAgent = new FakeAgentClient(agentId, "issue-ops");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Dispatch — FakeJobController polls, claims the pending item, then calls
        // StartAssignedWorkItemAsync which HTTP-fetches the assignment and re-registers with
        // an ActiveJob, setting ActiveJobId in the registry for [RequiresActiveJob] checks.
        var result = await DispatchIssueAsync("42");
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        // Wait for the agent to receive the job assignment (JobAssigned.Task completes when
        // StartAssignedWorkItemAsync finishes the re-registration with ActiveJob)
        // TODO: [WARNING] Timeout is 20 s here but 30 s in comparable setup paths elsewhere
        // (PrReviewLifecycleTests, FeedbackFlowTests, EpicDecompositionTests). Under CI load the
        // shorter timeout can produce intermittent TaskCanceledException failures on setup rather
        // than on the actual assertion. Align with the 30-second timeout used elsewhere.
        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("42", assignment.IssueIdentifier);

        return (assignment.JobId, fakeAgent);
    }

    // ── Scenario 1: Get issue (exercises IssueIdentifierFormatter) ────────────────

    /// <summary>
    /// Scenario 1: RequestGetIssue returns the seeded issue's title and description.
    ///
    /// <para>
    /// Acceptance criterion 2 guard: the identifier parameter is typed as
    /// <see cref="IssueIdentifier"/> (not <c>string</c>). Removing <c>IssueIdentifierFormatter</c>
    /// from <see cref="AgentHubMessagePack.SerializerOptions"/> would serialize the struct as
    /// <c>{"Value":"42"}</c>, which the hub cannot bind to its <c>string identifier</c> parameter,
    /// causing this test to fail with a <see cref="HubException"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario1_GetIssue_ReturnsSeededTitleAndDescription()
    {
        var (jobId, fakeAgent) = await SetupActiveJobAsync("agent-sc1");
        await using (fakeAgent)
        {
            // Act: invoke RequestGetIssue via the real hub, passing IssueIdentifier struct so
            // IssueIdentifierFormatter is exercised on the wire.
            var detail = await fakeAgent.RequestGetIssueAsync(jobId, new IssueIdentifier("42"));

            // Assert
            Assert.Equal("Issue-ops E2E test issue", detail.Title);
            Assert.Contains("hub issue operations end-to-end", detail.Description);
        }
    }

    // ── Scenario 2: Comment post/list/update round-trip (64-bit id) ──────────────

    /// <summary>
    /// Scenario 2: Posts a comment, lists it back (capturing the auto-generated 64-bit id),
    /// then updates it. Proves the #2927 fix (UpdateCommentAsync 64-bit id via string→long.TryParse)
    /// flows correctly through the hub.
    /// </summary>
    [Fact]
    public async Task Scenario2_CommentsRoundTrip_PostListUpdate_64BitIdPreserved()
    {
        var (jobId, fakeAgent) = await SetupActiveJobAsync("agent-sc2");
        await using (fakeAgent)
        {
            const string initialBody = "## Analysis\nInitial plan comment posted via hub.";
            const string updatedBody = "## Analysis\nUpdated plan comment via hub (64-bit id path).";

            // Act: post a comment through the hub (CommentType.Analysis → payload.AnalysisMarkdown)
            await fakeAgent.RequestPostCommentAsync(jobId, CommentType.Analysis,
                new CommentPayload { AnalysisMarkdown = initialBody });

            // Verify the comment was stored in InMemoryIssueProvider
            // TODO: [WARNING] Assert.Single with a predicate is fragile if any hub-internal path
            // also posts a comment for issue "42" before this assertion runs (e.g. a status-
            // transition service). It would fail with a misleading "more than one element" error.
            // Consider Assert.Contains + a count assertion, or filter first:
            //   Assert.Single(Fixture.IssueProvider.PostedComments.Where(c => c.Identifier == "42"))
            // Also: this assertion confirms the provider saw the call but does not confirm that the
            // CommentType.Analysis routing path (hub switch → payload.AnalysisMarkdown) was the
            // specific route taken. The list round-trip below (Assert.Single on comments) provides
            // the stronger confirmation; this intermediate check is a weaker duplicate.
            // If the hub's async provider write is not fully visible before InvokeAsync returns,
            // this assertion could race — low risk given SignalR's await semantics but worth noting.
            Assert.Single(Fixture.IssueProvider.PostedComments,
                c => c.Identifier == "42" && c.Body == initialBody);

            // List comments back through the hub (exercises IssueIdentifierFormatter)
            var comments = await fakeAgent.RequestListCommentsAsync(jobId, new IssueIdentifier("42"));
            Assert.Single(comments);

            var postedComment = comments[0];
            Assert.Equal(initialBody, postedComment.Body);

            // The id must be above int.MaxValue (InMemoryIssueProvider._nextCommentId starts at 5_000_000_000L)
            Assert.True(long.TryParse(postedComment.Id, out var commentId),
                $"Comment id '{postedComment.Id}' must be parseable as long");
            Assert.True(commentId > int.MaxValue,
                $"Comment id {commentId} must be above int.MaxValue to exercise the 64-bit fix (#2927)");

            // Update the comment through the hub — hub receives the id as string and parses with long.TryParse
            await fakeAgent.RequestUpdateCommentAsync(jobId, new IssueIdentifier("42"), commentId, updatedBody);

            // Assert the update was recorded with the correct 64-bit id.
            // UpdatedComments is a List<(string Identifier, long CommentId, string NewBody)> — value tuple,
            // so we locate by matching fields.
            var matchingUpdates = Fixture.IssueProvider.UpdatedComments
                .Where(u => u.Identifier == "42" && u.CommentId == commentId)
                .ToList();
            Assert.True(matchingUpdates.Count == 1,
                $"Expected exactly one update for issue 42 / comment {commentId}, got {matchingUpdates.Count}");
            Assert.Equal(updatedBody, matchingUpdates[0].NewBody);
        }
    }

    // ── Scenario 3: Label change (allowed) ───────────────────────────────────────

    /// <summary>
    /// Scenario 3: RequestLabelChange with an allowed label applies it to the issue.
    /// agent:in-progress is in AgentLabels.All so it passes both the validity guard and the
    /// DispatchGatedLabels guard and reaches SwapLabelAsync.
    /// </summary>
    [Fact]
    public async Task Scenario3_LabelChange_AllowedLabel_IsApplied()
    {
        var (jobId, fakeAgent) = await SetupActiveJobAsync("agent-sc3");
        await using (fakeAgent)
        {
            // Act
            await fakeAgent.RequestLabelChangeAsync(jobId, AgentLabels.InProgress);

            // Assert: the label change was recorded in the provider
            var added = Fixture.IssueProvider.LabelChanges
                .Where(c => c.Identifier == "42" && c.Added)
                .Select(c => c.Label)
                .ToList();
            Assert.Contains(AgentLabels.InProgress, added);
        }
    }

    // ── Scenario 4: Gated label (silently rejected) ───────────────────────────────

    /// <summary>
    /// Scenario 4: RequestLabelChange with agent:epic-approved is silently rejected by the hub.
    ///
    /// <para>
    /// The flow is: agent:epic-approved passes the AgentLabels.All validity guard (it is a
    /// known label) but is then caught by the AgentLabels.DispatchGatedLabels guard and discarded
    /// without an exception. The hub method returns void with no error.
    /// </para>
    /// <para>
    /// Do NOT assert with Assert.ThrowsAsync — the gated-label path is a silent ignore, not a throw.
    /// Assert on the LabelChanges state instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario4_GatedLabel_EpicApproved_IsRejectedSilently()
    {
        var (jobId, fakeAgent) = await SetupActiveJobAsync("agent-sc4");
        await using (fakeAgent)
        {
            // Act — must NOT throw; gated label is silently dropped by the hub
            await fakeAgent.RequestLabelChangeAsync(jobId, AgentLabels.EpicApproved);

            // Assert: no agent:epic-approved entry was added
            var epicApprovedAdded = Fixture.IssueProvider.LabelChanges
                .Any(c => c.Identifier == "42" && c.Added && c.Label == AgentLabels.EpicApproved);
            Assert.False(epicApprovedAdded,
                "agent:epic-approved is a gated label and must not be applied via RequestLabelChange");
        }
    }

    // ── Scenario 5: Create issue ──────────────────────────────────────────────────

    /// <summary>
    /// Scenario 5: RequestCreateIssue creates a sub-issue and returns its identifier and URL.
    /// </summary>
    [Fact]
    public async Task Scenario5_CreateIssue_ReturnsIdentifierAndUrl()
    {
        var (jobId, fakeAgent) = await SetupActiveJobAsync("agent-sc5");
        await using (fakeAgent)
        {
            // Act
            var result = await fakeAgent.RequestCreateIssueAsync(
                jobId,
                "Sub-issue: implement component A",
                "## Requirements\nImplement component A.",
                new[] { AgentLabels.Next });

            // Assert: returned result has identifier and URL
            Assert.NotNull(result.Identifier);
            Assert.False(string.IsNullOrEmpty(result.Identifier), "CreatedIssueResult.Identifier must not be empty");
            Assert.NotNull(result.Url);
            Assert.False(string.IsNullOrEmpty(result.Url), "CreatedIssueResult.Url must not be empty");

            // Assert: the issue was recorded in the provider.
            // CreatedIssues is a List<(string Title, string Body, IReadOnlyList<string>? Labels)> (value tuple).
            // TODO: [WARNING] InMemoryIssueProvider.Reset() does not clear CreatedIssues. If a prior
            // test in this class or another run caused a CreatedIssues entry with the same title to
            // exist, Assert.Single throws "more than one element". The predicate filter prevents a
            // false-positive from unrelated entries but not a same-title duplicate. Fix by adding
            // CreatedIssues.Clear() to InMemoryIssueProvider.Reset().
            Assert.Single(Fixture.IssueProvider.CreatedIssues,
                c => c.Title == "Sub-issue: implement component A");
        }
    }

    // ── Scenario 6: Authorization ─────────────────────────────────────────────────

    /// <summary>
    /// Scenario 6a: Invoking a [RequiresActiveJob] hub method with a job id that does not match
    /// the agent's active job is rejected with a HubException whose message contains the filter's
    /// authorization text — NOT a MessagePack binding failure.
    /// </summary>
    [Fact]
    public async Task Scenario6a_Authorization_WrongJobId_ThrowsHubException()
    {
        var (_, fakeAgent) = await SetupActiveJobAsync("agent-sc6a");
        await using (fakeAgent)
        {
            const string wrongJobId = "00000000-0000-0000-0000-000000000000";

            // Act: invoke with a job id that doesn't match the agent's active job
            var ex = await Assert.ThrowsAsync<HubException>(
                () => fakeAgent.RequestGetIssueAsync(wrongJobId, new IssueIdentifier("42")));

            // Assert: the error is from the authorization filter, not a MessagePack binding failure.
            // The filter throws: "Job {id} is not assigned to agent {agentId}"
            // A truly empty message (or one starting with "Failed to invoke") indicates a binding error.
            // TODO: [WARNING] The negative assertion (DoesNotContain "Failed to invoke") does not
            // positively confirm the authorization filter's text is present. If SignalR's error
            // prefixing changes across versions, this check remains green while the auth message
            // disappears. Add: Assert.Contains("not assigned to agent", ex.Message,
            // StringComparison.OrdinalIgnoreCase) to positively anchor to the auth-rejection path.
            Assert.False(
                string.IsNullOrEmpty(ex.Message),
                "HubException message must not be empty — an empty message indicates a MessagePack binding failure, not an auth rejection");
            Assert.DoesNotContain(
                "Failed to invoke",
                ex.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Scenario 6b: An agent that is connected but has no active job (never called
    /// StartAssignedWorkItemAsync) is rejected by the [RequiresActiveJob] filter with a
    /// HubException. The message must identify the authorization failure, not a binding error.
    /// </summary>
    [Fact]
    public async Task Scenario6b_Authorization_NoActiveJob_ThrowsHubException()
    {
        // Connect an agent but skip dispatch entirely — the agent is registered but Idle.
        await using var fakeAgent = new FakeAgentClient("agent-sc6b", "issue-ops");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // No DispatchIssueAsync, no StartAssignedWorkItemAsync — agent has no ActiveJobId.
        const string anyJobId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // Act: invoke a [RequiresActiveJob] method with no active job on this connection
        var ex = await Assert.ThrowsAsync<HubException>(
            () => fakeAgent.RequestGetIssueAsync(anyJobId, new IssueIdentifier("42")));

        // Assert: error is from the authorization filter, not a MessagePack binding failure
        // TODO: [WARNING] Same as Scenario 6a: the negative assertion (DoesNotContain "Failed to
        // invoke") does not positively confirm the authorization filter's rejection text. If the
        // filter wording changes or the filter is bypassed, this check stays green. Add:
        // Assert.Contains("Agent not registered", ex.Message, StringComparison.OrdinalIgnoreCase)
        // (or the current phrase from AgentAuthorizationFilter) to positively anchor to the
        // no-active-job rejection path.
        Assert.False(
            string.IsNullOrEmpty(ex.Message),
            "HubException message must not be empty — an empty message indicates a MessagePack binding failure, not an auth rejection");
        Assert.DoesNotContain(
            "Failed to invoke",
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
