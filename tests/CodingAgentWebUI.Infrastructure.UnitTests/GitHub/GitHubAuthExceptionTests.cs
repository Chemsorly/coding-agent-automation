using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for GitHubAuthException (Requirements 3.4–3.5).
/// Validates ErrorKind propagation, message preservation, and inner exception handling.
/// </summary>
public class GitHubAuthExceptionTests
{
    /// <summary>
    /// Requirement 3.4: ErrorKind is correctly set for PrivateKeyDecodeFailure.
    /// </summary>
    [Fact]
    public void Constructor_PrivateKeyDecodeFailure_SetsErrorKindCorrectly()
    {
        var exception = new GitHubAuthException(
            GitHubAuthErrorKind.PrivateKeyDecodeFailure,
            "Failed to decode private key");

        exception.ErrorKind.Should().Be(GitHubAuthErrorKind.PrivateKeyDecodeFailure);
    }

    /// <summary>
    /// Requirement 3.4: ErrorKind is correctly set for TokenExchangeFailure.
    /// </summary>
    [Fact]
    public void Constructor_TokenExchangeFailure_SetsErrorKindCorrectly()
    {
        var exception = new GitHubAuthException(
            GitHubAuthErrorKind.TokenExchangeFailure,
            "Token exchange failed");

        exception.ErrorKind.Should().Be(GitHubAuthErrorKind.TokenExchangeFailure);
    }

    /// <summary>
    /// Requirement 3.5: Message is preserved through the exception constructor.
    /// </summary>
    [Fact]
    public void Constructor_Message_IsPreserved()
    {
        const string expectedMessage = "GitHub App token exchange failed: 401 Unauthorized";

        var exception = new GitHubAuthException(
            GitHubAuthErrorKind.TokenExchangeFailure,
            expectedMessage);

        exception.Message.Should().Be(expectedMessage);
    }

    /// <summary>
    /// Requirement 3.5: InnerException is preserved when provided.
    /// </summary>
    [Fact]
    public void Constructor_WithInnerException_PreservesInnerException()
    {
        var innerException = new InvalidOperationException("Original error");

        var exception = new GitHubAuthException(
            GitHubAuthErrorKind.PrivateKeyDecodeFailure,
            "Failed to decode private key",
            innerException);

        exception.InnerException.Should().BeSameAs(innerException);
    }

    /// <summary>
    /// Requirement 3.5: InnerException is null when not provided.
    /// </summary>
    [Fact]
    public void Constructor_WithoutInnerException_InnerExceptionIsNull()
    {
        var exception = new GitHubAuthException(
            GitHubAuthErrorKind.TokenExchangeFailure,
            "Token exchange failed");

        exception.InnerException.Should().BeNull();
    }
}
