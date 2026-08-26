using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip property tests for <see cref="IssueDetail"/>
/// and a core subset of <see cref="PipelineConfiguration"/> fields.
///
/// IssueDetail: 5-key [MessagePackObject] embedded in every <see cref="JobAssignmentMessage"/>
/// dispatched over SignalR. No dedicated round-trip test existed.
///
/// PipelineConfiguration: 50+ key sealed record. Store-level property tests cover 4 fields;
/// this test covers the fields most likely to silently regress on a [Key] collision or
/// serialization edge case (string, TimeSpan, int, bool, enum, nullable, list).
/// </summary>
public class IssueDetailAndConfigMessagePackRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    // ── IssueDetail ───────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property IssueDetail_RoundTrip_PreservesAllFields()
    {
        var gen =
            from title in Gen.Elements("Fix login bug", "Add pagination endpoint", "Refactor auth service")
            from id in Gen.Elements("42", "123", "9999")
            from desc in Gen.Elements("User cannot log in", "API has no cursor pagination", "Auth module is too large")
            from labelCount in Gen.Choose(0, 4)
            from labels in Gen.ListOf(Gen.Elements("bug", "dotnet", "enhancement", "security", "priority:high"))
            from hasImages in Gen.Elements(true, false)
            select new IssueDetail
            {
                Title = title,
                Identifier = id,
                Description = desc,
                Labels = labels.Take(labelCount).ToList(),
                Images = hasImages
                    ? [new ImageReference { Url = "https://example.com/img.png", AltText = "screenshot", SourceType = ImageSourceType.Body, SourceIndex = 0 }]
                    : Array.Empty<ImageReference>()
            };

        return Prop.ForAll(gen.ToArbitrary(), (IssueDetail original) =>
        {
            var d = RoundTrip(original);

            d.Title.Should().Be(original.Title);
            d.Identifier.Should().Be(original.Identifier);
            d.Description.Should().Be(original.Description);
            d.Labels.Should().BeEquivalentTo(original.Labels, opts => opts.WithStrictOrdering());
            d.Images.Should().HaveCount(original.Images.Count);
            for (var i = 0; i < original.Images.Count; i++)
            {
                d.Images[i].Url.Should().Be(original.Images[i].Url);
                d.Images[i].AltText.Should().Be(original.Images[i].AltText);
            }
        });
    }

    [Fact]
    public void IssueDetail_EmptyLabels_SurvivesRoundTrip()
    {
        var original = new IssueDetail
        {
            Title = "Empty labels test",
            Identifier = "1",
            Description = "desc",
            Labels = []
        };

        var d = RoundTrip(original);

        d.Labels.Should().BeEmpty();
    }

    // ── PipelineConfiguration (core field subset) ─────────────────────────

    [Property(MaxTest = 20)]
    public Property PipelineConfiguration_CoreFields_SurviveRoundTrip()
    {
        var gen =
            from maxRetries in Gen.Choose(0, 20)
            from maxAnalysisRetries in Gen.Choose(0, 10)
            from agentTimeoutMin in Gen.Choose(1, 180)
            from workspace in Gen.Elements("/workspaces", "/tmp/agent", "/mnt/agent-workspaces")
            from failedRetentionDays in Gen.Choose(0, 30)
            from analysisEnabled in Gen.Elements(true, false)
            from closedLoopAutoStart in Gen.Elements(true, false)
            from heartbeatSweep in Gen.Choose(30, 120)
            select new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                MaxAnalysisRetries = maxAnalysisRetries,
                AgentTimeout = TimeSpan.FromMinutes(agentTimeoutMin),
                WorkspaceBaseDirectory = workspace,
                FailedWorkspaceRetentionDays = failedRetentionDays,
                AnalysisReviewEnabled = analysisEnabled,
                ClosedLoopAutoStart = closedLoopAutoStart,
                HeartbeatSweepIntervalSeconds = heartbeatSweep
            };

        return Prop.ForAll(gen.ToArbitrary(), (PipelineConfiguration original) =>
        {
            var d = RoundTrip(original);

            d.MaxRetries.Should().Be(original.MaxRetries);
            d.MaxAnalysisRetries.Should().Be(original.MaxAnalysisRetries);
            d.AgentTimeout.Should().Be(original.AgentTimeout);
            d.WorkspaceBaseDirectory.Should().Be(original.WorkspaceBaseDirectory);
            d.FailedWorkspaceRetentionDays.Should().Be(original.FailedWorkspaceRetentionDays);
            d.AnalysisReviewEnabled.Should().Be(original.AnalysisReviewEnabled);
            d.ClosedLoopAutoStart.Should().Be(original.ClosedLoopAutoStart);
            d.HeartbeatSweepIntervalSeconds.Should().Be(original.HeartbeatSweepIntervalSeconds);
        });
    }

    [Fact]
    public void PipelineConfiguration_BlacklistedPaths_SurvivesRoundTrip()
    {
        // Regression guard: BlacklistedPaths is a string[] with [Key(0)] — close to nullable keys
        var original = new PipelineConfiguration
        {
            BlacklistedPaths = [".agent", ".git", "node_modules"]
        };

        var d = RoundTrip(original);

        d.BlacklistedPaths.Should().BeEquivalentTo(original.BlacklistedPaths,
            opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void PipelineConfiguration_EmptyBlacklistedPaths_SurvivesRoundTrip()
    {
        var original = new PipelineConfiguration
        {
            BlacklistedPaths = []
        };

        var d = RoundTrip(original);

        d.BlacklistedPaths.Should().BeEmpty();
    }
}
