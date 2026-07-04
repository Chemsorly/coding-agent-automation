using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.UnitTests.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip tests for feedback DTOs.
/// These types are annotated with [MessagePackObject]/[Key(N)] and transit SignalR
/// via the default MessagePack protocol (ContractlessStandardResolverAllowPrivate).
/// Without these tests, adding a new [Key(N)] field or reordering keys can silently
/// corrupt feedback data in transit without any test failure.
/// </summary>
public class FeedbackMessagePackRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    // ── RunFeedback (full aggregate) ─────────────────────────────────────

    [Property(MaxTest = 20, Arbitrary = new[] { typeof(RunFeedbackSerializationArbitraries) })]
    public void RunFeedback_MessagePackRoundTrip_PreservesAllFields(RunFeedback original)
    {
        var deserialized = RoundTrip(original);

        deserialized.Should().NotBeNull();
        deserialized.Outcome.Should().Be(original.Outcome);
        deserialized.CollectedAtUtc.Should().Be(original.CollectedAtUtc);

        // Harness
        deserialized.Harness.Should().NotBeNull();
        deserialized.Harness.Category.Should().Be(original.Harness.Category);
        deserialized.Harness.StuckReason.Should().Be(original.Harness.StuckReason);
        deserialized.Harness.MissingContext.Should().BeEquivalentTo(original.Harness.MissingContext);
        deserialized.Harness.MissingCapabilities.Should().BeEquivalentTo(original.Harness.MissingCapabilities);
        deserialized.Harness.PromptIssues.Should().BeEquivalentTo(original.Harness.PromptIssues);
        deserialized.Harness.Suggestions.Should().BeEquivalentTo(original.Harness.Suggestions);

        // Issue (nullable)
        if (original.Issue is null)
        {
            deserialized.Issue.Should().BeNull();
        }
        else
        {
            deserialized.Issue.Should().NotBeNull();
            deserialized.Issue!.Category.Should().Be(original.Issue.Category);
            deserialized.Issue.Description.Should().Be(original.Issue.Description);
            deserialized.Issue.AffectedFiles.Should().BeEquivalentTo(original.Issue.AffectedFiles);
            deserialized.Issue.HumanActionNeeded.Should().Be(original.Issue.HumanActionNeeded);
        }
    }

    // ── HarnessFeedback (standalone) ─────────────────────────────────────

    [Property(MaxTest = 30)]
    public Property HarnessFeedback_MessagePackRoundTrip_PreservesFields()
    {
        var gen =
            from hasCategory in Gen.Elements(true, false)
            from hasSuggestion in Gen.Elements(true, false)
            select new HarnessFeedback
            {
                Category = hasCategory ? "test-category" : null,
                StuckReason = hasCategory ? "some reason" : null,
                MissingContext = hasCategory ? new List<string> { "file.ts" } : [],
                MissingCapabilities = [],
                PromptIssues = hasSuggestion ? new List<string> { "unclear prompt" } : [],
                Suggestions = hasSuggestion ? new List<string> { "add context" } : []
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.Category.Should().Be(original.Category);
            deserialized.StuckReason.Should().Be(original.StuckReason);
            deserialized.MissingContext.Should().BeEquivalentTo(original.MissingContext);
            deserialized.MissingCapabilities.Should().BeEquivalentTo(original.MissingCapabilities);
            deserialized.PromptIssues.Should().BeEquivalentTo(original.PromptIssues);
            deserialized.Suggestions.Should().BeEquivalentTo(original.Suggestions);
        });
    }

    // ── IssueFeedback (standalone) ───────────────────────────────────────

    [Property(MaxTest = 30)]
    public Property IssueFeedback_MessagePackRoundTrip_PreservesFields()
    {
        var gen =
            from hasCategory in Gen.Elements(true, false)
            from hasDescription in Gen.Elements(true, false)
            from hasAction in Gen.Elements(true, false)
            select new IssueFeedback
            {
                Category = hasCategory ? "contradictory criteria" : null,
                Description = hasDescription ? "Issue contradicts itself" : null,
                AffectedFiles = hasCategory ? new List<string> { "src/main.cs", "tests/main.test.cs" } : [],
                HumanActionNeeded = hasAction ? "Clarify acceptance criteria" : null
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.Category.Should().Be(original.Category);
            deserialized.Description.Should().Be(original.Description);
            deserialized.AffectedFiles.Should().BeEquivalentTo(original.AffectedFiles);
            deserialized.HumanActionNeeded.Should().Be(original.HumanActionNeeded);
        });
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void RunFeedback_EmptyLists_SurviveRoundTrip()
    {
        var original = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Harness = new HarnessFeedback
            {
                MissingContext = [],
                MissingCapabilities = [],
                PromptIssues = [],
                Suggestions = []
            },
            Issue = null
        };

        var deserialized = RoundTrip(original);

        deserialized.Harness.MissingContext.Should().BeEmpty();
        deserialized.Harness.MissingCapabilities.Should().BeEmpty();
        deserialized.Harness.PromptIssues.Should().BeEmpty();
        deserialized.Harness.Suggestions.Should().BeEmpty();
        deserialized.Issue.Should().BeNull();
    }

    [Fact]
    public void RunFeedback_AllNullOptionals_SurviveRoundTrip()
    {
        var original = new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            Harness = new HarnessFeedback
            {
                Category = null,
                StuckReason = null,
                MissingContext = [],
                MissingCapabilities = [],
                PromptIssues = [],
                Suggestions = []
            },
            Issue = new IssueFeedback
            {
                Category = null,
                Description = null,
                AffectedFiles = [],
                HumanActionNeeded = null
            }
        };

        var deserialized = RoundTrip(original);

        deserialized.Harness.Category.Should().BeNull();
        deserialized.Harness.StuckReason.Should().BeNull();
        deserialized.Issue.Should().NotBeNull();
        deserialized.Issue!.Category.Should().BeNull();
        deserialized.Issue.Description.Should().BeNull();
        deserialized.Issue.AffectedFiles.Should().BeEmpty();
        deserialized.Issue.HumanActionNeeded.Should().BeNull();
    }
}
