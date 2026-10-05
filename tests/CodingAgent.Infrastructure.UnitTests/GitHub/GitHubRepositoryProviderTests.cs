using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Infrastructure.GitHub;

namespace CodingAgent.Infrastructure.UnitTests;

public class GitHubRepositoryProviderTests
{
    // --- REQ-2.6: Vestigial static helpers removed ---

    [Theory]
    [InlineData("GenerateBranchName")]
    [InlineData("GeneratePrTitle")]
    [InlineData("GeneratePrBody")]
    [InlineData("GenerateCommitMessage")]
    public void GitHubRepositoryProvider_DoesNotContainStaticWrapperMethod(string methodName)
    {
        var methods = typeof(GitHubRepositoryProvider)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        methods.Should().NotContain(m => m.Name == methodName,
            $"GitHubRepositoryProvider should not contain '{methodName}' — it was moved to PipelineFormatting (REQ-2.6)");
    }

    [Fact]
    public void GitHubRepositoryProvider_DoesNotContainNonAlphanumericPattern()
    {
        var fields = typeof(GitHubRepositoryProvider)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        fields.Should().NotContain(f => f.Name.Contains("NonAlphanumeric", StringComparison.OrdinalIgnoreCase),
            "GitHubRepositoryProvider should not contain NonAlphanumericPattern — it was a duplicate of PipelineFormatting (REQ-2.6)");
    }

}
