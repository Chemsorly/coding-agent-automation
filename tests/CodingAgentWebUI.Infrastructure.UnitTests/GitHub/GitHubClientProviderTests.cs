using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using Moq;
using Octokit;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for GitHubClientProvider (Requirements 8.1–8.7).
/// Validates constructor null guards, client construction modes, and token retrieval.
/// </summary>
public class GitHubClientProviderTests
{
    private const string ValidApiUrl = "https://api.github.com";
    private const string ValidToken = "ghp_test_token_12345";

    #region Constructor Null Guards

    /// <summary>
    /// Requirement 8.1: Constructor with null apiUrl throws ArgumentNullException (dynamic token overload).
    /// </summary>
    [Fact]
    public void Constructor_DynamicToken_NullApiUrl_ThrowsArgumentNullException()
    {
        Func<CancellationToken, Task<string>> tokenProvider = _ => Task.FromResult("token");

        var act = () => new GitHubClientProvider(null!, tokenProvider);

        act.Should().Throw<ArgumentNullException>().WithParameterName("apiUrl");
    }

    /// <summary>
    /// Requirement 8.2: Constructor with null tokenProvider throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_DynamicToken_NullTokenProvider_ThrowsArgumentNullException()
    {
        var act = () => new GitHubClientProvider(ValidApiUrl, (Func<CancellationToken, Task<string>>)null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("tokenProvider");
    }

    /// <summary>
    /// Requirement 8.1: Constructor with null apiUrl throws ArgumentNullException (static token overload).
    /// </summary>
    [Fact]
    public void Constructor_StaticToken_NullApiUrl_ThrowsArgumentNullException()
    {
        string nullApiUrl = null!;

        var act = () => new GitHubClientProvider(nullApiUrl, ValidToken);

        act.Should().Throw<ArgumentNullException>().WithParameterName("apiUrl");
    }

    /// <summary>
    /// Requirement 8.1: Constructor with null token throws ArgumentNullException (static token overload).
    /// </summary>
    [Fact]
    public void Constructor_StaticToken_NullToken_ThrowsArgumentNullException()
    {
        string nullToken = null!;

        var act = () => new GitHubClientProvider(ValidApiUrl, nullToken);

        act.Should().Throw<ArgumentNullException>().WithParameterName("token");
    }

    /// <summary>
    /// Requirement 8.3: Constructor with null staticClient throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_PreBuiltClient_NullStaticClient_ThrowsArgumentNullException()
    {
        var act = () => new GitHubClientProvider((IGitHubClient)null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("staticClient");
    }

    #endregion

    #region GetClientAsync

    /// <summary>
    /// Requirement 8.4: GetClientAsync with dynamic token provider invokes provider and returns new client.
    /// </summary>
    [Fact]
    public async Task GetClientAsync_DynamicTokenProvider_InvokesProviderAndReturnsClient()
    {
        // Arrange
        var providerInvoked = false;
        Func<CancellationToken, Task<string>> tokenProvider = _ =>
        {
            providerInvoked = true;
            return Task.FromResult("dynamic-token");
        };
        var sut = new GitHubClientProvider(ValidApiUrl, tokenProvider);

        // Act
        var client = await sut.GetClientAsync(CancellationToken.None);

        // Assert
        providerInvoked.Should().BeTrue();
        client.Should().NotBeNull();
        client.Should().BeAssignableTo<IGitHubClient>();
    }

    /// <summary>
    /// Requirement 8.5: GetClientAsync with static token returns pre-built client without invoking token provider.
    /// </summary>
    [Fact]
    public async Task GetClientAsync_StaticClient_ReturnsPreBuiltClientWithoutInvokingTokenProvider()
    {
        // Arrange
        var mockClient = new Mock<IGitHubClient>();
        var sut = new GitHubClientProvider(mockClient.Object);

        // Act
        var client = await sut.GetClientAsync(CancellationToken.None);

        // Assert
        client.Should().BeSameAs(mockClient.Object);
    }

    /// <summary>
    /// Requirement 8.5: GetClientAsync with static token (string overload) returns client without invoking any provider.
    /// </summary>
    [Fact]
    public async Task GetClientAsync_StaticTokenString_ReturnsClientWithoutInvokingProvider()
    {
        // Arrange
        var sut = new GitHubClientProvider(ValidApiUrl, ValidToken);

        // Act
        var client = await sut.GetClientAsync(CancellationToken.None);

        // Assert
        client.Should().NotBeNull();
        client.Should().BeAssignableTo<IGitHubClient>();
    }

    #endregion

    #region GetTokenAsync

    /// <summary>
    /// Requirement 8.6: GetTokenAsync with dynamic token provider invokes provider and returns token string.
    /// </summary>
    [Fact]
    public async Task GetTokenAsync_DynamicTokenProvider_InvokesProviderAndReturnsToken()
    {
        // Arrange
        const string expectedToken = "dynamic-token-value";
        Func<CancellationToken, Task<string>> tokenProvider = _ => Task.FromResult(expectedToken);
        var sut = new GitHubClientProvider(ValidApiUrl, tokenProvider);

        // Act
        var token = await sut.GetTokenAsync(CancellationToken.None);

        // Assert
        token.Should().Be(expectedToken);
    }

    /// <summary>
    /// Requirement 8.7: GetTokenAsync with static token returns static token string.
    /// </summary>
    [Fact]
    public async Task GetTokenAsync_StaticToken_ReturnsStaticTokenString()
    {
        // Arrange
        var sut = new GitHubClientProvider(ValidApiUrl, ValidToken);

        // Act
        var token = await sut.GetTokenAsync(CancellationToken.None);

        // Assert
        token.Should().Be(ValidToken);
    }

    /// <summary>
    /// Requirement 8.7: GetTokenAsync with pre-built client and explicit token returns that token.
    /// </summary>
    [Fact]
    public async Task GetTokenAsync_PreBuiltClientWithToken_ReturnsProvidedToken()
    {
        // Arrange
        var mockClient = new Mock<IGitHubClient>();
        const string explicitToken = "explicit-static-token";
        var sut = new GitHubClientProvider(mockClient.Object, explicitToken);

        // Act
        var token = await sut.GetTokenAsync(CancellationToken.None);

        // Assert
        token.Should().Be(explicitToken);
    }

    #endregion
}
