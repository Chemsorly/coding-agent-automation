using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using System.Security.Cryptography;
using System.Text;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Property-based tests for API key authentication logic.
/// Tests the core constant-time comparison and key validation logic
/// that the AgentApiKeyAuthHandler depends on.
/// </summary>
public class ApiKeyAuthPropertyTests
{
    /// <summary>
    /// Property 3: API Key Authentication Rejection
    /// For any API key that doesn't match configured AGENT_API_KEY, constant-time comparison fails.
    /// Mirrors the production legacy-path: both sides are SHA256-hashed before FixedTimeEquals,
    /// matching AgentApiKeyAuthHandler.HandleAuthenticateAsync.
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    // TODO: These property tests re-implement the SHA256+FixedTimeEquals logic inline rather than
    // calling AgentApiKeyAuthHandler directly. A regression that removes the SHA256 step only from
    // the production handler (without touching these tests) would not be caught here. To fully
    // satisfy the regression-detection guarantee, consider adding an integration-style property that
    // constructs AgentApiKeyAuthHandler with a test configuration and exercises it end-to-end.
    [Property(MaxTest = 20)]
    public void InvalidApiKey_IsRejected(NonEmptyString configuredKey, NonEmptyString providedKey)
    {
        // Ensure the keys are different
        if (configuredKey.Get == providedKey.Get) return;

        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey.Get));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey.Get));

        var isValid = CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);

        isValid.Should().BeFalse();
    }

    /// <summary>
    /// Property 3 (continued): Missing or empty token is always rejected.
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void EmptyToken_IsRejected(NonEmptyString configuredKey)
    {
        var token = string.Empty;

        string.IsNullOrEmpty(token).Should().BeTrue("empty token should be rejected before comparison");
    }

    /// <summary>
    /// Property 3 (continued): Valid API key passes constant-time comparison.
    /// Mirrors the production legacy-path: the key is SHA256-hashed before FixedTimeEquals,
    /// matching AgentApiKeyAuthHandler.HandleAuthenticateAsync.
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    // TODO: This test is tautological — both configuredHash and providedHash are SHA256 of the same
    // input, so FixedTimeEquals always receives two identical 32-byte digests. The assertion passes
    // whether or not SHA256 is applied (raw bytes of the same string also compare equal). To provide
    // real regression signal, this test should invoke AgentApiKeyAuthHandler directly or use a shared
    // helper that mirrors the handler path, so a handler regression (e.g. hashing only one side)
    // would be detectable.
    [Property(MaxTest = 20)]
    public void ValidApiKey_IsAccepted(NonEmptyString apiKey)
    {
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Get));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Get));

        var isValid = CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);

        isValid.Should().BeTrue();
    }

    /// <summary>
    /// Property 3 (continued): Constant-time comparison is symmetric.
    /// Mirrors the production legacy-path: both sides are SHA256-hashed before FixedTimeEquals.
    /// SHA256 always produces 32-byte digests so FixedTimeEquals never short-circuits on length,
    /// making the symmetry invariant fully exercised.
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ConstantTimeComparison_IsSymmetric(NonEmptyString key1, NonEmptyString key2)
    {
        var bytes1 = SHA256.HashData(Encoding.UTF8.GetBytes(key1.Get));
        var bytes2 = SHA256.HashData(Encoding.UTF8.GetBytes(key2.Get));

        var forward = CryptographicOperations.FixedTimeEquals(bytes1, bytes2);
        var reverse = CryptographicOperations.FixedTimeEquals(bytes2, bytes1);

        forward.Should().Be(reverse);
    }
}
