using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class FeedbackRelayConfigFailureTests : HeadlessE2ETestBase
{
    public FeedbackRelayConfigFailureTests(E2EFixture fixture) : base(fixture) { }

    private InMemoryPipelineApiConfigClient SchedulerConfigClient =>
        (InMemoryPipelineApiConfigClient)Fixture.SchedulerFactory.Services
            .GetRequiredService<IPipelineApiConfigClient>();

    private int CommentsOn(string identifier) =>
        Fixture.IssueProvider.PostedComments.Count(c =>
            c.Identifier == identifier && c.Body.Contains(CommentMarkers.IssueFeedback));

    [Fact]
    public async Task WhenIssueProviderConfigsFailToLoad_ShouldLeaveRowsPending_AndDeliverLater()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8201",
            Title = "Config failure relay test 8201",
            Description = "Test issue for relay config failure",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8201", "8201", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8201",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        SchedulerConfigClient.FailProviderConfigLoadFor.Add(ProviderKind.Issue);
        try
        {
            // Act: sweep with failing issue provider config load
            await FeedbackOutboxTestData.SweepAsync(Fixture);

            // Assert: row stays pending, no comment posted
            var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8201");
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.ErrorMessage);
            Assert.Equal(0, CommentsOn("8201"));

            // Act: fix the failure and sweep again
            SchedulerConfigClient.FailProviderConfigLoadFor.Clear();
            await FeedbackOutboxTestData.SweepAsync(Fixture);

            // Assert: comment delivered, row completed
            Assert.Equal(1, CommentsOn("8201"));
            var rowAfter = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8201");
            Assert.Equal("Completed", rowAfter.Status);
        }
        finally
        {
            SchedulerConfigClient.FailProviderConfigLoadFor.Clear();
        }
    }

    [Fact]
    public async Task WhenIssueProviderConfigsKeepFailing_ShouldNeverExhaustTheRow()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { FeedbackCommentOutboxMaxAttempts = 2 },
            CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8202",
            Title = "Config failure relay test 8202",
            Description = "Test issue for relay config failure exhaustion",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8202", "8202", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8202",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        SchedulerConfigClient.FailProviderConfigLoadFor.Add(ProviderKind.Issue);
        try
        {
            // Act: sweep three times — more than maxAttempts (2)
            await FeedbackOutboxTestData.SweepAsync(Fixture);
            await FeedbackOutboxTestData.SweepAsync(Fixture);
            await FeedbackOutboxTestData.SweepAsync(Fixture);

            // Assert: row is still pending, attempt count was never incremented
            var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8202");
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0, row.AttemptCount);
        }
        finally
        {
            SchedulerConfigClient.FailProviderConfigLoadFor.Clear();
        }
    }

    [Fact]
    public async Task WhenRepositoryProviderConfigsFailToLoad_ShouldLeaveRowsPending()
    {
        await FeedbackOutboxTestData.ClearAsync(Fixture);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "8203",
            Title = "Config failure relay test 8203",
            Description = "Test issue for relay repo config failure",
            Labels = ["enhancement"]
        });

        await FeedbackOutboxTestData.EnqueueAsync(Fixture, "run-8203", "8203", "Needs clearer criteria");
        await FeedbackOutboxTestData.BackdateAsync(Fixture, "run-8203",
            FeedbackCommentRelayService.FastPathReservation + TimeSpan.FromMinutes(1));

        SchedulerConfigClient.FailProviderConfigLoadFor.Add(ProviderKind.Repository);
        try
        {
            // Act: sweep with failing repository provider config load (issue load succeeds)
            await FeedbackOutboxTestData.SweepAsync(Fixture);

            // Assert: row stays pending, no comment posted
            var row = await FeedbackOutboxTestData.ReadAsync(Fixture, "run-8203");
            Assert.Equal("Pending", row.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Equal(0, CommentsOn("8203"));
        }
        finally
        {
            SchedulerConfigClient.FailProviderConfigLoadFor.Clear();
        }
    }
}
