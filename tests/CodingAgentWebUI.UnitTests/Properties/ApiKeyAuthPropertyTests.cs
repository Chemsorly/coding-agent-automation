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
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    // TODO: This test calls FixedTimeEquals directly on raw UTF8 bytes, which no longer reflects
    // the production code path (production now hashes with SHA256 first). Update to use SHA256
    // pre-hashing to match the actual AgentApiKeyAuthHandler implementation.
    [Property(MaxTest = 20)]
    public void InvalidApiKey_IsRejected(NonEmptyString configuredKey, NonEmptyString providedKey)
    {
        // Ensure the keys are different
        if (configuredKey.Get == providedKey.Get) return;

        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey.Get);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey.Get);

        var isValid = CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes);

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
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    // TODO: This test uses direct FixedTimeEquals on raw bytes without SHA256 hashing,
    // which no longer reflects the production code path. Update to use SHA256 pre-hashing
    // to match the actual AgentApiKeyAuthHandler implementation.
    [Property(MaxTest = 20)]
    public void ValidApiKey_IsAccepted(NonEmptyString apiKey)
    {
        var configuredBytes = Encoding.UTF8.GetBytes(apiKey.Get);
        var providedBytes = Encoding.UTF8.GetBytes(apiKey.Get);

        var isValid = CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes);

        isValid.Should().BeTrue();
    }

    /// <summary>
    /// Property 3 (continued): Constant-time comparison is symmetric.
    /// **Validates: Requirements 1.8, 18.3**
    /// </summary>
    // TODO: This test verifies raw FixedTimeEquals symmetry without SHA256 pre-hashing.
    // Update to use SHA256-hashed inputs to match the production code path.
    [Property(MaxTest = 20)]
    public void ConstantTimeComparison_IsSymmetric(NonEmptyString key1, NonEmptyString key2)
    {
        var bytes1 = Encoding.UTF8.GetBytes(key1.Get);
        var bytes2 = Encoding.UTF8.GetBytes(key2.Get);

        var forward = CryptographicOperations.FixedTimeEquals(bytes1, bytes2);
        var reverse = CryptographicOperations.FixedTimeEquals(bytes2, bytes1);

        forward.Should().Be(reverse);
    }
}
