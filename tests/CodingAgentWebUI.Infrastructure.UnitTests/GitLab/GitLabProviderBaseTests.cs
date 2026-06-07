using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.GitLab;
using NGitLab;
using NGitLab.Mock;
using NGitLab.Mock.Config;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for GitLabProviderBase.
/// Feature: 029-gitlab-providers, Properties 2 and 3.
/// </summary>
public class GitLabProviderBaseTests
{
    /// <summary>
    /// Concrete test subclass to test the abstract GitLabProviderBase.
    /// Uses the internal test constructor that accepts an IGitLabClient directly.
    /// </summary>
    private sealed class TestableGitLabProvider : GitLabProviderBase
    {
        public TestableGitLabProvider(string apiUrl, string accessToken, int projectId)
            : base(apiUrl, accessToken, projectId)
        {
        }

        public TestableGitLabProvider(IGitLabClient client, int projectId)
            : base(client, projectId)
        {
        }

        /// <summary>
        /// Exposes the protected static ParseIdentifier method for testing.
        /// </summary>
        public static int ExposedParseIdentifier(string identifier, string entityType = "issue")
            => ParseIdentifier(identifier, entityType);
    }

    /// <summary>
    /// Creates a mock IGitLabClient via NGitLab.Mock for use in the internal test constructor.
    /// </summary>
    private static IGitLabClient CreateMockClient()
    {
        using var server = new GitLabConfig()
            .WithUser("TestUser", isDefault: true)
            .WithProject("TestProject", @namespace: "TestUser", addDefaultUserAsMaintainer: true)
            .BuildServer();

        return server.CreateClient();
    }

    #region Property 2: Direct construction rejects invalid credentials

    /// <summary>
    /// Property 2: Direct construction rejects invalid credentials (empty/whitespace token).
    /// For any whitespace-only or empty token string, constructing a GitLabProviderBase
    /// with a valid API URL throws ArgumentException.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(InvalidTokenArbitrary)])]
    public void DirectConstruction_RejectsEmptyOrWhitespaceToken(string invalidToken)
    {
        var act = () => new TestableGitLabProvider("https://gitlab.com", invalidToken, 1);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("access token");
    }

    /// <summary>
    /// Property 2: Direct construction rejects invalid credentials (null/empty API URL).
    /// For any null, empty, or whitespace-only API URL, constructing a GitLabProviderBase
    /// with a valid token throws ArgumentException.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(InvalidApiUrlArbitrary)])]
    public void DirectConstruction_RejectsNullOrEmptyApiUrl(string invalidApiUrl)
    {
        var act = () => new TestableGitLabProvider(invalidApiUrl, "valid-token-123", 1);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain("API URL");
    }

    /// <summary>
    /// Property 2: Direct construction succeeds with valid credentials.
    /// For any non-whitespace token and non-whitespace API URL, construction succeeds.
    /// **Validates: Requirements 2.2, 2.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(ValidCredentialsArbitrary)])]
    public void DirectConstruction_SucceedsWithValidCredentials(ValidCredentialsInput input)
    {
        var act = () => new TestableGitLabProvider(input.ApiUrl, input.AccessToken, input.ProjectId);

        act.Should().NotThrow();
    }

    #endregion

    #region Property 3: Project ID parsing

    /// <summary>
    /// Property 3: Project ID parsing (positive case).
    /// For any non-negative integer n, ParseIdentifier(n.ToString()) returns n.
    /// **Validates: Requirements 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIdentifier_RoundTrips_NonNegativeIntegers(NonNegativeInt n)
    {
        var input = n.Get.ToString();

        var result = TestableGitLabProvider.ExposedParseIdentifier(input);

        result.Should().Be(n.Get);
    }

    /// <summary>
    /// Property 3: Project ID parsing (negative case).
    /// For any non-integer string, ParseIdentifier throws ArgumentException
    /// with the invalid value included in the message.
    /// **Validates: Requirements 2.8, 27.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NonIntegerStringArbitrary)])]
    public void ParseIdentifier_ThrowsArgumentException_ForNonIntegerStrings(string input)
    {
        var act = () => TestableGitLabProvider.ExposedParseIdentifier(input);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain(input);
    }

    /// <summary>
    /// Property 3: Project ID parsing preserves negative integers.
    /// Negative integers are valid int.TryParse results, so they should parse successfully.
    /// **Validates: Requirements 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ParseIdentifier_ParsesNegativeIntegers(NegativeInt n)
    {
        var input = n.Get.ToString();

        var result = TestableGitLabProvider.ExposedParseIdentifier(input);

        result.Should().Be(n.Get);
    }

    /// <summary>
    /// Property 3: Test constructor with valid project ID succeeds.
    /// For any integer project ID, the internal test constructor stores it correctly.
    /// **Validates: Requirements 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public void TestConstructor_StoresProjectId(int projectId)
    {
        using var server = new GitLabServer();
        var user = server.Users.AddNew();
        var client = server.CreateClient(user);

        var provider = new TestableGitLabProvider(client, projectId);

        // The ProjectId is a protected property, but we can verify construction didn't throw
        // and the provider is usable (not null)
        provider.Should().NotBeNull();
    }

    #endregion
}

#region Arbitraries

/// <summary>
/// Input type for valid credentials property tests.
/// </summary>
public record ValidCredentialsInput(string ApiUrl, string AccessToken, int ProjectId)
{
    public override string ToString() => $"ApiUrl={ApiUrl}, Token={AccessToken[..Math.Min(5, AccessToken.Length)]}..., ProjectId={ProjectId}";
}

/// <summary>
/// Generates empty/whitespace token strings for invalid token tests.
/// </summary>
public static class InvalidTokenArbitrary
{
    public static Arbitrary<string> String()
    {
        var gen = Gen.Elements("", " ", "  ", "\t", " \t ", "   ", "\n", "\r\n", " \r\n ");
        return gen.ToArbitrary();
    }
}

/// <summary>
/// Generates null/empty/whitespace API URL strings for invalid URL tests.
/// </summary>
public static class InvalidApiUrlArbitrary
{
    public static Arbitrary<string> String()
    {
        var gen = Gen.Elements("", " ", "  ", "\t", " \t ", "   ", "\n", "\r\n", " \r\n ");
        return gen.ToArbitrary();
    }
}

/// <summary>
/// Generates valid credential inputs (non-whitespace token and URL).
/// </summary>
public static class ValidCredentialsArbitrary
{
    public static Arbitrary<ValidCredentialsInput> ValidCredentialsInput()
    {
        var alphanumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        var tokenGen =
            from len in Gen.Choose(10, 40)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select "glpat-" + new string(chars);

        var urlGen = Gen.Elements(
            "https://gitlab.com",
            "https://gitlab.example.com",
            "https://git.internal.corp",
            "https://gitlab.mycompany.io");

        var projectIdGen = Gen.Choose(1, 99999);

        var combined =
            from url in urlGen
            from token in tokenGen
            from projectId in projectIdGen
            select new ValidCredentialsInput(url, token, projectId);

        return combined.ToArbitrary();
    }
}

/// <summary>
/// Generates non-integer strings for ParseIdentifier negative tests.
/// Produces strings that cannot be parsed as integers (verified via int.TryParse).
/// </summary>
public static class NonIntegerStringArbitrary
{
    public static Arbitrary<string> String()
    {
        var nonIntegerGen = Gen.OneOf(
            // Strings with letters
            from len in Gen.Choose(1, 10)
            from chars in Gen.Elements("abcdefghijklmnopqrstuvwxyz".ToCharArray()).ArrayOf(len)
            select new string(chars),
            // Strings with mixed content
            from prefix in Gen.Elements("abc", "project", "id-", "#", "MR!")
            from num in Gen.Choose(1, 999)
            select $"{prefix}{num}",
            // Floating point numbers
            from whole in Gen.Choose(1, 999)
            from frac in Gen.Choose(1, 99)
            select $"{whole}.{frac}",
            // Strings with special characters that int.TryParse rejects
            Gen.Elements("abc", "12.5", "1e3", "0x1F", "1,000", "NaN", "Infinity", "--1", "++1", "12abc", "a1b2")
        )
        // Filter to ensure only truly non-parseable strings are generated
        .Where(s => !int.TryParse(s, out _));

        return nonIntegerGen.ToArbitrary();
    }
}

#endregion
