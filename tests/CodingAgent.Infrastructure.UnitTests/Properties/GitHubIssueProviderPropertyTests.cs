using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodingAgent.Infrastructure.UnitTests;

/// <summary>
/// Property-based tests for GitHubIssueProvider.
/// Feature: provider-interface-gaps
/// </summary>
[Collection("GitHubTelemetry")]
public class GitHubIssueProviderPropertyTests
{
    /// <summary>
    /// Feature: provider-interface-gaps, Property 5: Label Pass-Through to the issues query
    /// For any non-null, non-empty list of label strings, calling ListOpenIssuesAsync
    /// with that label list sends every one of those labels in the query of the list request.
    /// When the label list is null or empty, no label filter is sent.
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(LabelListArbitrary)])]
    public void ListOpenIssuesAsync_Labels_PassedThrough_To_RepositoryIssueRequest(LabelInput input)
    {
        // Arrange: Octokit prepends /api/v3 to the path of a client with a non-github.com URL (see WireMockTestBase.ApiPath)
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/v3/repos/owner/repo/issues").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("[]"));

        var provider = new GitHubIssueProvider(new GitHubConnectionInfo(server.Url!, "owner", "repo"), "fake-token");

        // Act
        provider.ListOpenIssuesAsync(1, 10, input.Labels, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        var request = server.LogEntries.Should().ContainSingle().Subject.RequestMessage!;
        var query = Uri.UnescapeDataString(request.RawQuery ?? string.Empty);

        if (input.Labels is { Count: > 0 })
        {
            // Non-null, non-empty → every label must be sent
            query.Should().Contain("labels=");
            foreach (var label in input.Labels)
                query.Should().Contain(label);
        }
        else
        {
            // Null or empty → no label filter applied
            query.Should().NotMatchRegex("(^|[?&])labels=[^&]");
        }
    }
}

/// <summary>
/// Wrapper type for label list inputs that may be null, empty, or non-empty.
/// </summary>
public sealed class LabelInput
{
    public IReadOnlyList<string>? Labels { get; }
    public LabelInput(IReadOnlyList<string>? labels) => Labels = labels;
    public override string ToString() =>
        Labels is null ? "null" : $"[{string.Join(", ", Labels)}]";
}

/// <summary>
/// FsCheck arbitrary that generates label list inputs covering null, empty, and non-empty cases.
/// </summary>
public static class LabelListArbitrary
{
    private static readonly string[] SampleLabels =
    [
        "bug", "enhancement", "documentation", "help-wanted",
        "good-first-issue", "priority-high", "priority-low", "backend",
        "frontend", "security", "performance", "breaking-change",
        "wontfix", "duplicate", "invalid"
    ];

    public static Arbitrary<LabelInput> LabelInputs()
    {
        var nullGen = Gen.Constant(new LabelInput(null));
        var emptyGen = Gen.Constant(new LabelInput(Array.Empty<string>()));

        var nonEmptyGen =
            from count in Gen.Choose(1, 5)
            from indices in Gen.ArrayOf(Gen.Choose(0, SampleLabels.Length - 1), count)
            let labels = indices.Select(i => SampleLabels[i]).Distinct().ToList().AsReadOnly()
            select new LabelInput(labels);

        return Gen.OneOf(nullGen, emptyGen, nonEmptyGen, nonEmptyGen, nonEmptyGen, nonEmptyGen)
            .ToArbitrary();
    }
}
