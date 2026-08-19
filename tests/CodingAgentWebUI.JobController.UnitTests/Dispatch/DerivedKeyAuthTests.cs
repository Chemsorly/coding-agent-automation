using CodingAgentWebUI.JobController.Dispatch;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Verifies the round-trip: DeriveAgentKey(master, jobName) produces a token
/// that AgentApiKeyAuthHandler would accept when ?agentId=jobName is passed.
/// Tests the HMAC encoding mismatch fix from the Spec 043 code review (CRITICAL 1+2).
/// </summary>
public sealed class DerivedKeyAuthTests
{
    [Fact]
    public void DerivedKey_AuthenticatesSuccessfully_WhenJobNameUsedAsAgentId()
    {
        // Verifies the round-trip: DeriveAgentKey(master, jobName) produces a token
        // that AgentApiKeyAuthHandler accepts when ?agentId=jobName is passed.
        var masterKey = "test-master-key-abc123";
        var workItemId = Guid.NewGuid();
        var jobName = DispatchLoop.GenerateJobName(workItemId);

        var derivedKey = DispatchLoop.DeriveAgentKey(masterKey, jobName);

        // Simulate what AgentApiKeyAuthHandler does:
        // re-derives using HMAC-SHA256(masterKey, agentId=jobName) → lowercase hex
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(masterKey);
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(jobName);
        var expectedHash = System.Security.Cryptography.HMACSHA256.HashData(keyBytes, dataBytes);
        var expectedKey = Convert.ToHexString(expectedHash).ToLowerInvariant();

        // Constant-time comparison (same SHA256-of-SHA256 approach as the handler)
        var tokenHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(derivedKey));
        var expectedKeyHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedKey));

        Assert.True(
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(tokenHash, expectedKeyHash),
            $"DeriveAgentKey output '{derivedKey}' does not match what AgentApiKeyAuthHandler expects '{expectedKey}'");
    }

    [Fact]
    public void DerivedKey_IsDifferentForDifferentJobNames()
    {
        var masterKey = "test-master-key-abc123";
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var key1 = DispatchLoop.DeriveAgentKey(masterKey, DispatchLoop.GenerateJobName(id1));
        var key2 = DispatchLoop.DeriveAgentKey(masterKey, DispatchLoop.GenerateJobName(id2));

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DerivedKey_IsLowercaseHex_NotBase64()
    {
        var masterKey = "test-master-key";
        var jobName = DispatchLoop.GenerateJobName(Guid.NewGuid());
        var key = DispatchLoop.DeriveAgentKey(masterKey, jobName);

        // Lowercase hex: only 0-9 and a-f, 64 chars for SHA256
        Assert.Matches("^[0-9a-f]{64}$", key);
    }
}
