using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Wire-contract tests for <see cref="JobAssignmentMessage.ReviewPrAuthorVerified"/>: the flag round-trips over
/// MessagePack, a JSON payload from an older API without it reads false, and MessagePack key 47 belongs to it alone.
/// The default is false, so each test starts from true: a value of false would pass with the member lost.
/// </summary>
public class JobAssignmentMessageVerificationWireTests
{
    private const int ReviewPrAuthorVerifiedKey = 47;

    private static JobAssignmentMessage MakeMessage(bool reviewPrAuthorVerified) => new()
    {
        JobId = "job-1",
        IssueIdentifier = "42",
        IssueDetail = new IssueDetail
        {
            Identifier = "42",
            Title = "Add pagination",
            Description = "Pages the API",
            Labels = [],
            IsAuthorVerified = false
        },
        ParsedIssue = new ParsedIssue
        {
            RequirementsSection = string.Empty,
            AcceptanceCriteria = []
        },
        IssueComments = [],
        RepoProviderConfigId = "repo-config-1",
        AgentProviderConfigId = "agent-config-1",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        InitiatedBy = "test",
        QualityGateConfigs = [],
        RunType = PipelineRunType.Review,
        ReviewPrAuthor = "alice",
        ReviewPrAuthorVerified = reviewPrAuthorVerified
    };

    [Fact]
    public void ReviewPrAuthorVerified_MessagePack_RoundTrip()
    {
        var bytes = MessagePackSerializer.Serialize(MakeMessage(true), AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<JobAssignmentMessage>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.ReviewPrAuthorVerified.Should().BeTrue();
        deserialized.ReviewPrAuthor.Should().Be("alice");
    }

    [Fact]
    public void ReviewPrAuthorVerified_Json_PayloadWithoutTheField_ReadsFalse()
    {
        var json = JsonSerializer.Serialize(MakeMessage(true), PipelineJsonOptions.Default);
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("reviewPrAuthorVerified").Should().BeTrue("the payload must be written without the new member");

        var deserialized = JsonSerializer.Deserialize<JobAssignmentMessage>(node.ToJsonString(), PipelineJsonOptions.Default);

        deserialized.Should().NotBeNull("a payload from an API older than the field must still load");
        deserialized!.ReviewPrAuthorVerified.Should().BeFalse("a missing flag reads as not verified");
        deserialized.ReviewPrAuthor.Should().Be("alice");
    }

    [Fact]
    public void Key47_IsReviewPrAuthorVerified()
    {
        var withKey47 = typeof(JobAssignmentMessage)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<KeyAttribute>()?.IntKey == ReviewPrAuthorVerifiedKey)
            .Select(p => p.Name)
            .ToList();

        withKey47.Should().Equal([nameof(JobAssignmentMessage.ReviewPrAuthorVerified)],
            "key 47 is the flag's, and no other member of the message may share it");
    }
}
