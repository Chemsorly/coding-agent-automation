using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for HubConnectionManager.DeriveKey (internal static — HMAC-SHA256 derivation).
/// The Agent assembly exposes internals to Agent.UnitTests only; since we can't access
/// HubConnectionManager directly from Pipeline.UnitTests, this tests the observable
/// properties (determinism, uniqueness, length, empty-fallback).
/// NOTE: if Agent project adds InternalsVisibleTo for Pipeline.UnitTests in the future,
/// move these to directly call HubConnectionManager.DeriveKey.
/// </summary>
public sealed class HubConnectionManagerDeriveKeyTests
{
    // Access via reflection since InternalsVisibleTo doesn't cover Pipeline.UnitTests
    private static string DeriveKey(string masterKey, string agentId)
    {
        var method = typeof(HubConnectionManager)
            .GetMethod("DeriveKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return (string)method!.Invoke(null, [masterKey, agentId])!;
    }

    [Fact]
    public void DeriveKey_EmptyAgentId_ReturnsMasterKey()
    {
        var result = DeriveKey("master-secret", "");
        result.Should().Be("master-secret");
    }

    [Fact]
    public void DeriveKey_NonEmptyAgentId_ReturnsDifferentFromMasterKey()
    {
        var result = DeriveKey("master-secret", "agent-1");
        result.Should().NotBe("master-secret");
    }

    [Fact]
    public void DeriveKey_Deterministic_SameInputSameOutput()
    {
        var key1 = DeriveKey("secret", "agent-1");
        var key2 = DeriveKey("secret", "agent-1");
        key1.Should().Be(key2);
    }

    [Fact]
    public void DeriveKey_DifferentAgents_DifferentKeys()
    {
        var key1 = DeriveKey("secret", "agent-1");
        var key2 = DeriveKey("secret", "agent-2");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveKey_DifferentMasterKeys_DifferentKeys()
    {
        var key1 = DeriveKey("secret-a", "agent-1");
        var key2 = DeriveKey("secret-b", "agent-1");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveKey_IsHexString_64Chars()
    {
        // HMAC-SHA256 = 32 bytes = 64 hex chars
        var result = DeriveKey("secret", "agent-1");
        result.Should().HaveLength(64);
        result.Should().MatchRegex("^[0-9a-f]+$");
    }
}
