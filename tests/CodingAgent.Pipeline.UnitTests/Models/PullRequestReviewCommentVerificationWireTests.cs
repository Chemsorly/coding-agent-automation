using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Wire-contract tests for <see cref="PullRequestReviewComment.IsVerified"/>: the flag is read only by
/// <c>SharedPrOperations.FinalizeReviewComments</c> in the process that sets it, so it stays off both the
/// MessagePack and the JSON wire.
/// </summary>
public class PullRequestReviewCommentVerificationWireTests
{
    private static PullRequestReviewComment Comment(bool? isVerified) => new()
    {
        Author = "alice",
        Body = "hello",
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Id = "1",
        Path = "src/a.cs",
        IsVerified = isVerified
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void MessagePack_RoundTrip_DoesNotCarryIsVerified(bool? value)
    {
        var bytes = MessagePackSerializer.Serialize(Comment(value), AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<PullRequestReviewComment>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsVerified.Should().BeNull("the flag is not on the wire, so it reads null, which counts as verified");
        deserialized.Author.Should().Be("alice");
        deserialized.Path.Should().Be("src/a.cs");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Json_RoundTrip_DoesNotCarryIsVerified(bool value)
    {
        var json = JsonSerializer.Serialize(Comment(value), PipelineJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<PullRequestReviewComment>(json, PipelineJsonOptions.Default);

        json.Should().NotContainEquivalentOf("isVerified");
        deserialized!.IsVerified.Should().BeNull();
        deserialized.Id.Should().Be("1");
    }
}
