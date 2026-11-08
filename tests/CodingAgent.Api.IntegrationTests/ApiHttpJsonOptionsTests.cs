using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// The API's HTTP JSON options (the ones <c>[FromBody]</c> binding uses) must read an <see cref="IssueDetail"/> that
/// has no <c>isAuthorVerified</c>: a Scheduler still on the previous release posts work items without it, and the
/// C# <c>required</c> member would otherwise make the request a 400 during the rollout.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ApiHttpJsonOptionsTests
{
    private readonly ApiWebApplicationFactory _factory;

    public ApiHttpJsonOptionsTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private JsonSerializerOptions HttpOptions() =>
        _factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

    [Fact]
    public void ApiHttpJsonOptions_IssueDetailWithoutTheField_ReadsFalse()
    {
        const string json = """{"description":"d","identifier":"1","labels":[],"title":"t"}""";

        var deserialized = JsonSerializer.Deserialize<IssueDetail>(json, HttpOptions());

        deserialized.Should().NotBeNull();
        deserialized!.IsAuthorVerified.Should().BeFalse("a body from a Scheduler on the previous release has no flag");
        deserialized.Title.Should().Be("t");
    }

    [Fact]
    public void ApiHttpJsonOptions_IssueDetailWithTheField_KeepsIt()
    {
        const string json = """{"description":"d","identifier":"1","labels":[],"title":"t","isAuthorVerified":true}""";

        var deserialized = JsonSerializer.Deserialize<IssueDetail>(json, HttpOptions());

        deserialized!.IsAuthorVerified.Should().BeTrue();
    }

    [Fact]
    public void ApiHttpJsonOptions_IssueDetailWithoutTitle_StillThrows()
    {
        const string json = """{"description":"d","identifier":"1","labels":[],"isAuthorVerified":true}""";

        var act = () => JsonSerializer.Deserialize<IssueDetail>(json, HttpOptions());

        act.Should().Throw<JsonException>("only the flag is relaxed; the other required members stay enforced");
    }
}
