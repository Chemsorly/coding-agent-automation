using System.Security.Cryptography;
using System.Text;

namespace CodingAgent.Pipeline;

/// <summary>
/// Per-agent API key derivation, shared by the code that issues agent keys (the per-Job key
/// Secret) and the code that verifies them (<c>AgentApiKeyAuthHandler</c>), so the two cannot drift.
/// </summary>
public static class AgentKeyDerivation
{
    /// <summary>
    /// Returns <c>HMAC-SHA256(masterKey, agentId)</c> as lowercase hex: the bearer token an agent
    /// presents together with <c>?agentId={agentId}</c>. An agent pod's ID is its Job name.
    /// </summary>
    public static string DeriveAgentKey(string masterKey, string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterKey);
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(masterKey), Encoding.UTF8.GetBytes(agentId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
