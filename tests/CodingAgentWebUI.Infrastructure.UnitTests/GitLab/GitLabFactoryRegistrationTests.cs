using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for ProviderFactory GitLab registration and validation.
/// Feature: 029-gitlab-providers, Property 1.
/// </summary>
public class GitLabFactoryRegistrationTests
{
    private static readonly string[] RequiredGitLabKeys =
    [
        ProviderSettingKeys.ApiUrl,
        ProviderSettingKeys.AccessToken,
        ProviderSettingKeys.ProjectId
    ];

    private static ProviderFactory CreateFactory()
        => new(new PipelineConfiguration());

    #region Property 1: Factory validation reports all missing settings

    /// <summary>
    /// Property 1: Factory validation reports all missing settings.
    /// For any non-empty subset of required GitLab settings that are missing from the config,
    /// the exception message contains all missing key names.
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(Arbitrary = [typeof(MissingSettingsSubsetArbitrary)])]
    public void ValidateRequiredSettings_ReportsAllMissingKeys_InExceptionMessage(MissingSettingsInput input)
    {
        // Arrange — build a config with only the "present" keys populated
        var settings = new Dictionary<string, string>();
        foreach (var key in RequiredGitLabKeys)
        {
            if (!input.MissingKeys.Contains(key))
            {
                settings[key] = key switch
                {
                    ProviderSettingKeys.ApiUrl => "https://gitlab.com",
                    ProviderSettingKeys.AccessToken => "glpat-valid-token-123",
                    ProviderSettingKeys.ProjectId => "42",
                    _ => "value"
                };
            }
        }

        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = settings
        };

        // Act
        var act = () => ProviderFactory.ValidateRequiredSettings(config, RequiredGitLabKeys);

        // Assert — exception message contains every missing key
        var exception = act.Should().Throw<ArgumentException>().Which;
        foreach (var missingKey in input.MissingKeys)
        {
            exception.Message.Should().Contain(missingKey,
                $"missing key '{missingKey}' should appear in the exception message");
        }

        exception.ParamName.Should().Be("config");
    }

    /// <summary>
    /// Property 1: Factory validation also detects whitespace-only values as missing.
    /// For any non-empty subset of required settings set to whitespace-only values,
    /// the exception message contains all those key names.
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(Arbitrary = [typeof(WhitespaceSettingsSubsetArbitrary)])]
    public void ValidateRequiredSettings_ReportsWhitespaceOnlyValues_AsMissing(WhitespaceSettingsInput input)
    {
        // Arrange — build a config where some keys have whitespace-only values
        var settings = new Dictionary<string, string>();
        foreach (var key in RequiredGitLabKeys)
        {
            if (input.WhitespaceKeys.Contains(key))
            {
                settings[key] = input.WhitespaceValue;
            }
            else
            {
                settings[key] = key switch
                {
                    ProviderSettingKeys.ApiUrl => "https://gitlab.com",
                    ProviderSettingKeys.AccessToken => "glpat-valid-token-123",
                    ProviderSettingKeys.ProjectId => "42",
                    _ => "value"
                };
            }
        }

        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = settings
        };

        // Act
        var act = () => ProviderFactory.ValidateRequiredSettings(config, RequiredGitLabKeys);

        // Assert — exception message contains every whitespace-only key
        var exception = act.Should().Throw<ArgumentException>().Which;
        foreach (var wsKey in input.WhitespaceKeys)
        {
            exception.Message.Should().Contain(wsKey,
                $"whitespace-only key '{wsKey}' should appear in the exception message");
        }
    }

    /// <summary>
    /// Property 1: Factory validation passes when all required settings are present and non-whitespace.
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(Arbitrary = [typeof(ValidGitLabSettingsArbitrary)])]
    public void ValidateRequiredSettings_DoesNotThrow_WhenAllKeysPresent(ValidGitLabSettingsInput input)
    {
        // Arrange
        var settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
            [ProviderSettingKeys.AccessToken] = input.AccessToken,
            [ProviderSettingKeys.ProjectId] = input.ProjectId
        };

        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = settings
        };

        // Act
        var act = () => ProviderFactory.ValidateRequiredSettings(config, RequiredGitLabKeys);

        // Assert
        act.Should().NotThrow();
    }

    /// <summary>
    /// Property 1: CreateIssueProvider throws with all missing keys when settings are incomplete.
    /// Tests the full factory path (not just the static helper).
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(Arbitrary = [typeof(MissingSettingsSubsetArbitrary)])]
    public void CreateIssueProvider_ThrowsWithAllMissingKeys(MissingSettingsInput input)
    {
        // Arrange
        var factory = CreateFactory();
        var settings = new Dictionary<string, string>();
        foreach (var key in RequiredGitLabKeys)
        {
            if (!input.MissingKeys.Contains(key))
            {
                settings[key] = key switch
                {
                    ProviderSettingKeys.ApiUrl => "https://gitlab.com",
                    ProviderSettingKeys.AccessToken => "glpat-valid-token-123",
                    ProviderSettingKeys.ProjectId => "42",
                    _ => "value"
                };
            }
        }

        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitLab",
            DisplayName = "Test GitLab",
            Settings = settings
        };

        // Act
        var act = () => factory.CreateIssueProvider(config);

        // Assert
        var exception = act.Should().Throw<ArgumentException>().Which;
        foreach (var missingKey in input.MissingKeys)
        {
            exception.Message.Should().Contain(missingKey);
        }
    }

    #endregion
}

#region Arbitraries for Property 1

/// <summary>
/// Input for missing settings subset tests.
/// </summary>
public record MissingSettingsInput(IReadOnlyList<string> MissingKeys)
{
    public override string ToString() => $"Missing=[{string.Join(", ", MissingKeys)}]";
}

/// <summary>
/// Generates non-empty subsets of the required GitLab settings keys.
/// </summary>
public static class MissingSettingsSubsetArbitrary
{
    private static readonly string[] AllKeys =
    [
        ProviderSettingKeys.ApiUrl,
        ProviderSettingKeys.AccessToken,
        ProviderSettingKeys.ProjectId
    ];

    public static Arbitrary<MissingSettingsInput> MissingSettingsInput()
    {
        // Generate non-empty subsets by generating a bitmask (1-7 for 3 keys)
        var gen =
            from bitmask in Gen.Choose(1, (1 << AllKeys.Length) - 1)
            let missing = AllKeys
                .Where((_, i) => (bitmask & (1 << i)) != 0)
                .ToList()
            select new MissingSettingsInput(missing.AsReadOnly());

        return gen.ToArbitrary();
    }
}

/// <summary>
/// Input for whitespace-only settings tests.
/// </summary>
public record WhitespaceSettingsInput(IReadOnlyList<string> WhitespaceKeys, string WhitespaceValue)
{
    public override string ToString() => $"WhitespaceKeys=[{string.Join(", ", WhitespaceKeys)}], Value='{WhitespaceValue}'";
}

/// <summary>
/// Generates non-empty subsets of required keys paired with whitespace-only values.
/// </summary>
public static class WhitespaceSettingsSubsetArbitrary
{
    private static readonly string[] AllKeys =
    [
        ProviderSettingKeys.ApiUrl,
        ProviderSettingKeys.AccessToken,
        ProviderSettingKeys.ProjectId
    ];

    public static Arbitrary<WhitespaceSettingsInput> WhitespaceSettingsInput()
    {
        var whitespaceGen = Gen.Elements("", " ", "  ", "\t", " \t ", "   ");

        var gen =
            from bitmask in Gen.Choose(1, (1 << AllKeys.Length) - 1)
            from ws in whitespaceGen
            let keys = AllKeys
                .Where((_, i) => (bitmask & (1 << i)) != 0)
                .ToList()
            select new WhitespaceSettingsInput(keys.AsReadOnly(), ws);

        return gen.ToArbitrary();
    }
}

/// <summary>
/// Input for valid GitLab settings tests.
/// </summary>
public record ValidGitLabSettingsInput(string ApiUrl, string AccessToken, string ProjectId)
{
    public override string ToString() => $"ApiUrl={ApiUrl}, Token={AccessToken[..Math.Min(5, AccessToken.Length)]}..., ProjectId={ProjectId}";
}

/// <summary>
/// Generates valid (non-whitespace) GitLab settings values.
/// </summary>
public static class ValidGitLabSettingsArbitrary
{
    public static Arbitrary<ValidGitLabSettingsInput> ValidGitLabSettingsInput()
    {
        var urlGen = Gen.Elements(
            "https://gitlab.com",
            "https://gitlab.example.com",
            "https://git.internal.corp");

        var tokenGen =
            from len in Gen.Choose(10, 30)
            from chars in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()).ArrayOf(len)
            select "glpat-" + new string(chars);

        var projectIdGen =
            from id in Gen.Choose(1, 99999)
            select id.ToString();

        var gen =
            from url in urlGen
            from token in tokenGen
            from projectId in projectIdGen
            select new ValidGitLabSettingsInput(url, token, projectId);

        return gen.ToArbitrary();
    }
}

#endregion
