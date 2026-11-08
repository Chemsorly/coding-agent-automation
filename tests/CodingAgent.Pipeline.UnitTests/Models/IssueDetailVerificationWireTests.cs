using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Wire-contract tests for the author-verified flag on <see cref="IssueDetail"/>, <see cref="LinkedIssueContext"/>
/// and <see cref="IssueSummary"/>: the flag round-trips over MessagePack and JSON, and data written before the field
/// existed reads false. <see cref="IssueDetail.IsAuthorVerified"/> is C# <c>required</c>, which System.Text.Json
/// enforces, so the JSON tests also pin that only this one member is relaxed (<see cref="PipelineJsonOptions.Resolver"/>).
/// </summary>
public class IssueDetailVerificationWireTests
{
    private const string CamelCaseWithoutTheField = """{"description":"d","identifier":"1","labels":[],"title":"t"}""";
    private const string PascalCaseWithoutTheField = """{"Description":"d","Identifier":"1","Labels":[],"Title":"t"}""";
    private const string CamelCaseWithoutTitle = """{"description":"d","identifier":"1","labels":[],"isAuthorVerified":true}""";

    private static JsonSerializerOptions OptionsNamed(string name) => name switch
    {
        nameof(PipelineJsonOptions.Default) => PipelineJsonOptions.Default,
        nameof(PipelineJsonOptions.Lenient) => PipelineJsonOptions.Lenient,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown options")
    };

    private static IssueDetail MakeIssue(bool isAuthorVerified) => new()
    {
        Description = "body",
        Identifier = "42",
        Labels = ["bug", "dotnet"],
        Title = "Fix it",
        Images = [new ImageReference { Url = "https://example.com/a.png", AltText = "a", SourceType = ImageSourceType.Body, SourceIndex = 0 }],
        Url = "https://example.com/issues/42",
        CreatedAt = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
        IsAuthorVerified = isAuthorVerified
    };

    // ─── IssueDetail: MessagePack ──────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MessagePack_RoundTrip_KeepsIsAuthorVerified(bool value)
    {
        var bytes = MessagePackSerializer.Serialize(MakeIssue(value), AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<IssueDetail>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsAuthorVerified.Should().Be(value);
        deserialized.Identifier.Should().Be("42");
        deserialized.Title.Should().Be("Fix it");
        deserialized.Url.Should().Be("https://example.com/issues/42");
    }

    [Fact]
    public void MessagePack_PayloadFromAnOlderApi_ReadsFalse()
    {
        // A 7-element array (keys 0-6) as an API from before the field wrote it: key 7 is absent.
        var bufferWriter = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);
        writer.WriteArrayHeader(7);
        writer.Write("old body");        // key 0: Description
        writer.Write("7");               // key 1: Identifier
        writer.WriteArrayHeader(1);      // key 2: Labels
        writer.Write("bug");
        writer.Write("Old issue");       // key 3: Title
        writer.WriteArrayHeader(0);      // key 4: Images
        writer.Write("https://example.com/issues/7"); // key 5: Url
        writer.WriteNil();               // key 6: CreatedAt
        writer.Flush();

        var deserialized = MessagePackSerializer.Deserialize<IssueDetail>(
            bufferWriter.WrittenSpan.ToArray(), AgentHubMessagePack.SerializerOptions);

        deserialized.IsAuthorVerified.Should().BeFalse("an older API payload without key 7 reads as not verified");
        deserialized.Description.Should().Be("old body");
        deserialized.Identifier.Should().Be("7");
        deserialized.Labels.Should().Equal("bug");
        deserialized.Title.Should().Be("Old issue");
        deserialized.Url.Should().Be("https://example.com/issues/7");
    }

    // ─── IssueDetail: JSON ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(PipelineJsonOptions.Default), CamelCaseWithoutTheField)]
    [InlineData(nameof(PipelineJsonOptions.Lenient), CamelCaseWithoutTheField)]
    [InlineData(nameof(PipelineJsonOptions.Lenient), PascalCaseWithoutTheField)]
    public void Json_PayloadWithoutTheField_ReadsFalse(string optionsName, string json)
    {
        var act = () => JsonSerializer.Deserialize<IssueDetail>(json, OptionsNamed(optionsName));

        var deserialized = act.Should().NotThrow("JSON written before the field existed must still load").Subject;
        deserialized.Should().NotBeNull();
        deserialized!.IsAuthorVerified.Should().BeFalse();
        deserialized.Identifier.Should().Be("1");
        deserialized.Title.Should().Be("t");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Json_RoundTrip_KeepsIsAuthorVerified(bool value)
    {
        var json = JsonSerializer.Serialize(MakeIssue(value), PipelineJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<IssueDetail>(json, PipelineJsonOptions.Default);

        json.Should().Contain("\"isAuthorVerified\"", "the flag is written even when false, so a reader can tell it was decided");
        deserialized.Should().NotBeNull();
        deserialized!.IsAuthorVerified.Should().Be(value);
        deserialized.Identifier.Should().Be("42");
    }

    [Theory]
    [InlineData(nameof(PipelineJsonOptions.Default))]
    [InlineData(nameof(PipelineJsonOptions.Lenient))]
    public void Json_OtherRequiredMembersStayEnforced(string optionsName)
    {
        var act = () => JsonSerializer.Deserialize<IssueDetail>(CamelCaseWithoutTitle, OptionsNamed(optionsName));

        act.Should().Throw<JsonException>("only IsAuthorVerified is relaxed; a missing title is still refused");
    }

    // ─── LinkedIssueContext ────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkedIssueContext_MessagePack_RoundTrip_KeepsIsAuthorVerified(bool value)
    {
        var linked = new LinkedIssueContext { Description = "d", Identifier = "9", Title = "t", IsAuthorVerified = value };

        var bytes = MessagePackSerializer.Serialize(linked, AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<LinkedIssueContext>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsAuthorVerified.Should().Be(value);
        deserialized.Identifier.Should().Be("9");
    }

    [Theory]
    [InlineData(nameof(PipelineJsonOptions.Default))]
    [InlineData(nameof(PipelineJsonOptions.Lenient))]
    public void LinkedIssueContext_Json_PayloadWithoutTheField_ReadsFalse(string optionsName)
    {
        const string json = """{"description":"d","identifier":"9","title":"t"}""";

        var deserialized = JsonSerializer.Deserialize<LinkedIssueContext>(json, OptionsNamed(optionsName));

        deserialized.Should().NotBeNull();
        deserialized!.IsAuthorVerified.Should().BeFalse("a work item stored before the field has no isAuthorVerified");
        deserialized.Identifier.Should().Be("9");
    }

    // ─── IssueSummary ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IssueSummary_MessagePack_RoundTrip_KeepsIsAuthorVerified(bool value)
    {
        var summary = new IssueSummary { Identifier = "5", Title = "t", Labels = ["bug"], IsAuthorVerified = value };

        var bytes = MessagePackSerializer.Serialize(summary, AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<IssueSummary>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsAuthorVerified.Should().Be(value);
        deserialized.Identifier.Should().Be("5");
    }

    // ─── WithImages ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithImages_KeepsEveryOtherField(bool isAuthorVerified)
    {
        var original = MakeIssue(isAuthorVerified);
        IReadOnlyList<ImageReference> images =
        [
            new ImageReference { Url = "https://example.com/b.png", AltText = "b", SourceType = ImageSourceType.Comment, SourceIndex = 2 }
        ];

        var copy = original.WithImages(images);

        copy.Images.Should().Equal(images);
        copy.Description.Should().Be(original.Description);
        copy.Identifier.Should().Be(original.Identifier);
        copy.Labels.Should().Equal(original.Labels);
        copy.Title.Should().Be(original.Title);
        copy.Url.Should().Be(original.Url);
        copy.CreatedAt.Should().Be(original.CreatedAt);
        copy.IsAuthorVerified.Should().Be(isAuthorVerified);
        original.Images.Should().HaveCount(1, "the original is not changed");
    }
}
