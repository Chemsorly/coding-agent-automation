using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for TokenVendingService GitLab config pass-through.
/// Feature: 029-gitlab-providers, Property 18.
/// </summary>
public class GitLabTokenVendingTests
{
    private static TokenVendingService CreateService()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var httpClient = new HttpClient();
        return new TokenVendingService(logger, httpClient);
    }

    #region Property 18: Token vending passes GitLab configs through correctly

    /// <summary>
    /// Property 18: Token vending passes GitLab configs through correctly.
    /// For any GitLab ProviderConfig with an AccessToken setting, PrepareAgentConfigsAsync
    /// produces a config where:
    /// - The "token" key contains the original AccessToken value
    /// - The "accessToken" key is removed
    /// - No JWT exchange is attempted (no privateKeyBase64 present)
    /// **Validates: Requirements 21.1, 21.3, 21.4**
    /// </summary>
    [Property(Arbitrary = [typeof(GitLabTokenVendingInputArbitrary)])]
    public void PrepareAgentConfigs_CopiesAccessTokenToTokenKey_AndRemovesAccessToken(GitLabTokenVendingInput input)
    {
        // Arrange
        var service = CreateService();

        var settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
            [ProviderSettingKeys.AccessToken] = input.AccessToken,
            [ProviderSettingKeys.ProjectId] = input.ProjectId
        };

        var config = new ProviderConfig
        {
            Kind = input.Kind,
            ProviderType = "GitLab",
            DisplayName = input.DisplayName,
            Settings = settings
        };

        var configs = new List<ProviderConfig> { config }.AsReadOnly();

        // Act
        var result = service.PrepareAgentConfigsAsync(configs, "repo-config-id", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        result.Should().HaveCount(1);
        var prepared = result[0];

        // Token key should contain the original AccessToken value
        prepared.Settings.Should().ContainKey(ProviderSettingKeys.Token);
        prepared.Settings[ProviderSettingKeys.Token].Should().Be(input.AccessToken,
            "the token key should contain the original AccessToken value");

        // AccessToken key should be removed
        prepared.Settings.Should().NotContainKey(ProviderSettingKeys.AccessToken,
            "the accessToken key should be removed after copying to token");
    }

    /// <summary>
    /// Property 18: Token vending preserves other settings unchanged.
    /// For any GitLab config, non-token settings (ApiUrl, ProjectId) are preserved as-is.
    /// **Validates: Requirements 21.1, 21.3**
    /// </summary>
    [Property(Arbitrary = [typeof(GitLabTokenVendingInputArbitrary)])]
    public void PrepareAgentConfigs_PreservesOtherSettings(GitLabTokenVendingInput input)
    {
        // Arrange
        var service = CreateService();

        var settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
            [ProviderSettingKeys.AccessToken] = input.AccessToken,
            [ProviderSettingKeys.ProjectId] = input.ProjectId
        };

        var config = new ProviderConfig
        {
            Kind = input.Kind,
            ProviderType = "GitLab",
            DisplayName = input.DisplayName,
            Settings = settings
        };

        var configs = new List<ProviderConfig> { config }.AsReadOnly();

        // Act
        var result = service.PrepareAgentConfigsAsync(configs, "repo-config-id", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        var prepared = result[0];
        prepared.Settings[ProviderSettingKeys.ApiUrl].Should().Be(input.ApiUrl);
        prepared.Settings[ProviderSettingKeys.ProjectId].Should().Be(input.ProjectId);
        prepared.ProviderType.Should().Be("GitLab");
        prepared.DisplayName.Should().Be(input.DisplayName);
        prepared.Kind.Should().Be(input.Kind);
    }

    /// <summary>
    /// Property 18: Token vending does not attempt JWT exchange for GitLab configs.
    /// GitLab configs have no privateKeyBase64, so they should never trigger the GitHub App
    /// token generation path.
    /// **Validates: Requirements 21.4**
    /// </summary>
    [Property(Arbitrary = [typeof(GitLabTokenVendingInputArbitrary)])]
    public void PrepareAgentConfigs_DoesNotContainPrivateKeyBase64_InOutput(GitLabTokenVendingInput input)
    {
        // Arrange
        var service = CreateService();

        var settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
            [ProviderSettingKeys.AccessToken] = input.AccessToken,
            [ProviderSettingKeys.ProjectId] = input.ProjectId
        };

        var config = new ProviderConfig
        {
            Kind = input.Kind,
            ProviderType = "GitLab",
            DisplayName = input.DisplayName,
            Settings = settings
        };

        var configs = new List<ProviderConfig> { config }.AsReadOnly();

        // Act
        var result = service.PrepareAgentConfigsAsync(configs, "repo-config-id", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert — no privateKeyBase64 in output (confirms no JWT path taken)
        var prepared = result[0];
        prepared.Settings.Should().NotContainKey(ProviderSettingKeys.PrivateKeyBase64,
            "GitLab configs should never have privateKeyBase64 in the output");
    }

    #endregion
}

#region Arbitraries for Property 18

/// <summary>
/// Input for GitLab token vending property tests.
/// </summary>
public record GitLabTokenVendingInput(
    string ApiUrl,
    string AccessToken,
    string ProjectId,
    string DisplayName,
    ProviderKind Kind)
{
    public override string ToString() =>
        $"ApiUrl={ApiUrl}, Token={AccessToken[..Math.Min(5, AccessToken.Length)]}..., ProjectId={ProjectId}, Kind={Kind}";
}

/// <summary>
/// Generates valid GitLab token vending inputs with non-whitespace AccessToken values.
/// </summary>
public static class GitLabTokenVendingInputArbitrary
{
    public static Arbitrary<GitLabTokenVendingInput> GitLabTokenVendingInput()
    {
        var urlGen = Gen.Elements(
            "https://gitlab.com",
            "https://gitlab.example.com",
            "https://git.internal.corp",
            "https://gitlab.mycompany.io");

        var tokenGen =
            from len in Gen.Choose(10, 40)
            from chars in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray()).ArrayOf(len)
            select "glpat-" + new string(chars);

        var projectIdGen =
            from id in Gen.Choose(1, 99999)
            select id.ToString();

        var displayNameGen = Gen.Elements(
            "GitLab Repo",
            "My Project",
            "Team CI/CD",
            "Internal GitLab");

        var kindGen = Gen.Elements(
            ProviderKind.Issue,
            ProviderKind.Repository,
            ProviderKind.Pipeline);

        var gen =
            from url in urlGen
            from token in tokenGen
            from projectId in projectIdGen
            from displayName in displayNameGen
            from kind in kindGen
            select new GitLabTokenVendingInput(url, token, projectId, displayName, kind);

        return gen.ToArbitrary();
    }
}

#endregion
