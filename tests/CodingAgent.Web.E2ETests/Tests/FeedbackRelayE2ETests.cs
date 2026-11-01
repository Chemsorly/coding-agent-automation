using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FeedbackRelayE2ETests : HeadlessE2ETestBase
{
    public FeedbackRelayE2ETests(E2EFixture fixture) : base(fixture) { }

    private int CommentsOn(string identifier) =>
        Fixture.IssueProvider.PostedComments.Count(c =>
            c.Identifier == identifier && c.Body.Contains(CommentMarkers.IssueFeedback));

    [Fact]
    public async Task WhenARowIsWithinTheReservation_ShouldNotPost()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8101",
            Title = "Relay test 8101",
            Description = "Test issue for relay reservation check",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8101", "8101", "Needs clearer criteria");

        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(0, CommentsOn("8101"));
        var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8101");
        Assert.Equal("Pending", row.Status);
        Assert.Equal(0, row.AttemptCount);
    }

    [Fact]
    public async Task WhenTheReservationHasExpired_ShouldPostOnce_AndComplete()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8102",
            Title = "Relay test 8102",
            Description = "Test issue for relay delivery",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8102", "8102", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8102",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(1, CommentsOn("8102"));
        var comment = Fixture.IssueProvider.PostedComments
            .First(c => c.Identifier == "8102" && c.Body.Contains(CommentMarkers.IssueFeedback));
        Assert.Contains("Needs clearer criteria", comment.Body);
        var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8102");
        Assert.Equal("Completed", row.Status);
        Assert.NotNull(row.CompletedAt);

        // Second sweep must not post again — relay skips Completed rows
        await FeedbackOutboxTestData.SweepAsync(Fixture);
        Assert.Equal(1, CommentsOn("8102"));
    }

    [Fact]
    public async Task WhenDeliveryFails_ShouldStayPending_AndARetryPostsOnce()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8103",
            Title = "Relay test 8103",
            Description = "Test issue for relay retry",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8103", "8103", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8103",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        Fixture.IssueProvider.InitializeShouldFail = true;
        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(0, CommentsOn("8103"));
        var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8103");
        Assert.Equal("Pending", row.Status);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal("Fake initialize failure", row.ErrorMessage);

        Fixture.IssueProvider.InitializeShouldFail = false;
        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(1, CommentsOn("8103"));
        var rowAfter = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8103");
        Assert.Equal("Completed", rowAfter.Status);
    }

    [Fact]
    public async Task WhenAttemptsAreExhausted_ShouldMarkTheRowFailed_AndStopRetrying()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { FeedbackCommentOutboxMaxAttempts = 2 },
            CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8104",
            Title = "Relay test 8104",
            Description = "Test issue for relay exhaustion",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8104", "8104", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8104",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        Fixture.IssueProvider.InitializeShouldFail = true;
        await FeedbackOutboxTestData.SweepAsync(Fixture);
        await FeedbackOutboxTestData.SweepAsync(Fixture);

        var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8104");
        Assert.Equal("Failed", row.Status);
        Assert.Equal(2, row.AttemptCount);

        // After exhaustion, relay ignores the row even when delivery would succeed
        Fixture.IssueProvider.InitializeShouldFail = false;
        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(0, CommentsOn("8104"));
        var rowFinal = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8104");
        Assert.Equal("Failed", rowFinal.Status);
    }

    [Fact]
    public async Task WhenTheFastPathDelivered_ShouldNotPostAgainAfterTheReservation()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8105",
            Title = "Relay test 8105",
            Description = "Test issue for fast path regression",
            Labels = ["enhancement", "agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-relay-5",
            Name = "Relay Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-relay-5",
            DisplayName = "Relay Test Profile",
            JobTemplate = E2ERouting.Key("e2e"),
            Enabled = true
        });

        await using var fakeAgent = new FakeAgentClient("relay-agent-1", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await DispatchIssueAsync("8105");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await fakeAgent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/8105",
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = DateTime.UtcNow,
                Harness = new HarnessFeedback(),
                Issue = new IssueFeedback
                {
                    Category = "vague",
                    Description = "Fast path text"
                }
            }
        });

        // Poll until the fast path has marked the outbox row Completed.
        // Uses SingleOrDefaultAsync (not SingleAsync) so that if the row does not yet exist
        // when the first poll fires — bookkeeping runs after the WorkItem is terminal — the
        // lambda returns false instead of throwing InvalidOperationException.
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var row = await db.FeedbackCommentOutbox
                .AsNoTracking()
                .SingleOrDefaultAsync(f => f.RunId == assignment.JobId);
            return row?.Status == "Completed";
        });

        Assert.Equal(1, CommentsOn("8105"));

        // Backdate past the reservation and sweep — relay must not post a second comment
        await FeedbackOutboxTestData.BackdateAsync(Fixture, assignment.JobId,
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(1, CommentsOn("8105"));
    }

    [Fact]
    public async Task WhenTheFastPathAbandonedARow_ShouldDeliverItAfterTheReservation_NotBefore()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8106",
            Title = "Relay test 8106",
            Description = "Test issue for relay boundary",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8106", "8106", "Needs clearer criteria");

        // Fresh row — within reservation, should not post
        await FeedbackOutboxTestData.SweepAsync(Fixture);
        Assert.Equal(0, CommentsOn("8106"));

        // 14 minutes old — still within the 15-minute reservation
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8106", TimeSpan.FromMinutes(14));
        await FeedbackOutboxTestData.SweepAsync(Fixture);
        Assert.Equal(0, CommentsOn("8106"));

        // 16 minutes old — past the reservation, relay should deliver
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8106", TimeSpan.FromMinutes(16));
        await FeedbackOutboxTestData.SweepAsync(Fixture);

        Assert.Equal(1, CommentsOn("8106"));
        var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8106");
        Assert.Equal("Completed", row.Status);
    }
}
