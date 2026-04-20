using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Providers;
using Moq;
using Octokit;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for GitHubIssueProvider.
/// Feature: provider-interface-gaps
/// </summary>
public class GitHubIssueProviderPropertyTests
{
    /// <summary>
    /// Feature: provider-interface-gaps, Property 5: Label Pass-Through to Octokit
    /// For any non-null, non-empty list of label strings, calling ListOpenIssuesAsync
    /// with that label list results in RepositoryIssueRequest.Labels containing exactly
    /// those labels. When the label list is null or empty, no label filter is applied.
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(LabelListArbitrary)])]
    public void ListOpenIssuesAsync_Labels_PassedThrough_To_RepositoryIssueRequest(LabelInput input)
    {
        // Arrange
        var mockClient = new Mock<IGitHubClient>();
        var mockIssues = new Mock<IIssuesClient>();
        mockClient.Setup(c => c.Issue).Returns(mockIssues.Object);

        RepositoryIssueRequest? capturedRequest = null;
        mockIssues
            .Setup(i => i.GetAllForRepository("owner", "repo",
                It.IsAny<RepositoryIssueRequest>(), It.IsAny<ApiOptions>()))
            .Callback<string, string, RepositoryIssueRequest, ApiOptions>(
                (_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new List<Issue>().AsReadOnly());

        var provider = new GitHubIssueProvider(mockClient.Object, "owner", "repo");

        // Act
        provider.ListOpenIssuesAsync(1, 10, input.Labels, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        capturedRequest.Should().NotBeNull();

        if (input.Labels is { Count: > 0 })
        {
            // Non-null, non-empty → labels must be passed through exactly
            capturedRequest!.Labels.Should().BeEquivalentTo(input.Labels);
        }
        else
        {
            // Null or empty → no label filter applied
            capturedRequest!.Labels.Should().BeEmpty();
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
