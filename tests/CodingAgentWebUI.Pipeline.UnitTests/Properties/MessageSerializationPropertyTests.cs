using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for MessagePack serialization round-trip fidelity.
/// </summary>
public class MessageSerializationPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            MessagePack.Resolvers.ContractlessStandardResolver.Instance);

    /// <summary>
    /// Property 18: Message Serialization Round-Trip Fidelity
    /// For any AgentRegistrationMessage, serialize then deserialize produces equivalent object.
    /// **Validates: Requirements 15.3, 15.4, 15.5, 15.6, 15.7**
    /// </summary>
    [Property]
    public void AgentRegistrationMessage_RoundTrip(
        NonEmptyString agentId,
        NonEmptyString hostname,
        NonEmptyString agentType,
        NonEmptyString[] labels)
    {
        var original = new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = hostname.Get,
            AgentType = agentType.Get,
            Labels = labels.Select(l => l.Get).ToList()
        };

        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<AgentRegistrationMessage>(bytes, MsgPackOptions);

        deserialized.AgentId.Should().Be(original.AgentId);
        deserialized.Hostname.Should().Be(original.Hostname);
        deserialized.AgentType.Should().Be(original.AgentType);
        deserialized.Labels.Should().BeEquivalentTo(original.Labels);
    }

    /// <summary>
    /// Property 18 (continued): HeartbeatMessage round-trip with DateTimeOffset and nullable PipelineStep.
    /// **Validates: Requirements 15.3, 15.4, 15.5, 15.6, 15.7**
    /// </summary>
    [Property]
    public void HeartbeatMessage_RoundTrip(
        NonEmptyString agentId,
        bool hasCurrentStep,
        long memoryUsageMb)
    {
        var original = new HeartbeatMessage
        {
            AgentId = agentId.Get,
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = hasCurrentStep ? PipelineStep.AnalyzingCode : null,
            MemoryUsageMb = memoryUsageMb
        };

        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<HeartbeatMessage>(bytes, MsgPackOptions);

        deserialized.AgentId.Should().Be(original.AgentId);
        deserialized.Timestamp.Should().BeCloseTo(original.Timestamp, TimeSpan.FromSeconds(1));
        deserialized.CurrentStep.Should().Be(original.CurrentStep);
        deserialized.MemoryUsageMb.Should().Be(original.MemoryUsageMb);
    }

    /// <summary>
    /// Property 18 (continued): JobCompletionPayload round-trip with all fields.
    /// **Validates: Requirements 15.3, 15.4, 15.5, 15.6, 15.7**
    /// </summary>
    [Property]
    public void JobCompletionPayload_RoundTrip(
        bool isDraftPr,
        int retryCount,
        int filesChanged,
        bool brainUpdatesPushed,
        bool isRework)
    {
        var original = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            FailureReason = isDraftPr ? "Quality gates failed" : null,
            PullRequestUrl = "https://github.com/test/pr/1",
            PullRequestNumber = "42",
            IsDraftPr = isDraftPr,
            RetryCount = retryCount,
            CompletedAt = DateTimeOffset.UtcNow,
            FilesChangedCount = filesChanged,
            LinesAdded = 100,
            LinesRemoved = 50,
            BrainUpdatesPushed = brainUpdatesPushed,
            AnalysisRecommendation = "ready",
            IsRework = isRework,
            AnalysisConcerns = new[] { "concern1" },
            AnalysisBlockingIssues = new List<string>(),
            BlacklistedFilesDetected = new[] { ".agent/test" },
            CodeReviewAgentsRun = new[] { "Correctness" },
            CodeReviewCriticalCount = 0,
            CodeReviewWarningCount = 1,
            CodeReviewSuggestionCount = 3,
            FinalLabel = isDraftPr ? AgentLabels.Error : AgentLabels.Done
        };

        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<JobCompletionPayload>(bytes, MsgPackOptions);

        deserialized.FinalStep.Should().Be(original.FinalStep);
        deserialized.FailureReason.Should().Be(original.FailureReason);
        deserialized.PullRequestUrl.Should().Be(original.PullRequestUrl);
        deserialized.PullRequestNumber.Should().Be(original.PullRequestNumber);
        deserialized.IsDraftPr.Should().Be(original.IsDraftPr);
        deserialized.RetryCount.Should().Be(original.RetryCount);
        deserialized.CompletedAt.Should().BeCloseTo(original.CompletedAt, TimeSpan.FromSeconds(1));
        deserialized.FilesChangedCount.Should().Be(original.FilesChangedCount);
        deserialized.BrainUpdatesPushed.Should().Be(original.BrainUpdatesPushed);
        deserialized.IsRework.Should().Be(original.IsRework);
        deserialized.AnalysisConcerns.Should().BeEquivalentTo(original.AnalysisConcerns);
        deserialized.BlacklistedFilesDetected.Should().BeEquivalentTo(original.BlacklistedFilesDetected);
        deserialized.CodeReviewAgentsRun.Should().BeEquivalentTo(original.CodeReviewAgentsRun);
        deserialized.FinalLabel.Should().Be(original.FinalLabel);
    }

    /// <summary>
    /// Property 18 (continued): TokenRefreshResponse round-trip with DateTimeOffset.
    /// **Validates: Requirements 15.3, 15.4, 15.5, 15.6, 15.7**
    /// </summary>
    [Property]
    public void TokenRefreshResponse_RoundTrip(NonEmptyString token)
    {
        var original = new TokenRefreshResponse
        {
            Token = token.Get,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<TokenRefreshResponse>(bytes, MsgPackOptions);

        deserialized.Token.Should().Be(original.Token);
        deserialized.ExpiresAt.Should().BeCloseTo(original.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Property 18 (continued): CommentPayload round-trip with nullable fields.
    /// **Validates: Requirements 15.3, 15.4, 15.5, 15.6, 15.7**
    /// </summary>
    [Property]
    public void CommentPayload_RoundTrip(bool hasAnalysis, bool hasAssessment)
    {
        var original = new CommentPayload
        {
            AnalysisMarkdown = hasAnalysis ? "## Analysis\nSome content" : null,
            AssessmentJson = hasAssessment ? "{\"recommendation\":\"ready\"}" : null
        };

        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<CommentPayload>(bytes, MsgPackOptions);

        deserialized.AnalysisMarkdown.Should().Be(original.AnalysisMarkdown);
        deserialized.AssessmentJson.Should().Be(original.AssessmentJson);
    }
}
