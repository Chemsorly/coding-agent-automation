using System.Text.Json.Serialization;
using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Why an MCP or web tool call failed. Decided from the error text by <c>McpFailureClassifier</c>;
/// <see cref="Unknown"/> when the agent tool reports no error text (Kiro).
/// </summary>
public enum McpFailureCategory
{
    /// <summary>The server or its backend could not be reached: timeout, refused or closed connection, 502/503/504.</summary>
    Connection,

    /// <summary>The server or its backend refused the credentials: 401/403.</summary>
    Auth,

    /// <summary>Any other error, usually the agent's own request (bad query, unknown resource, 404).</summary>
    Other,

    /// <summary>The agent tool reported the failure without a reason.</summary>
    Unknown
}

/// <summary>The calls of one tool (<c>server/tool</c>) and how many of them failed, by cause.</summary>
[MessagePackObject]
public sealed record McpCallCounts
{
    [Key(0)] public int Calls { get; init; }
    [Key(1)] public int FailedConnection { get; init; }
    [Key(2)] public int FailedAuth { get; init; }
    [Key(3)] public int FailedOther { get; init; }
    [Key(4)] public int FailedUnknown { get; init; }

    /// <summary>All failed calls.</summary>
    [JsonIgnore, IgnoreMember]
    public int Failed => FailedConnection + FailedAuth + FailedOther + FailedUnknown;

    /// <summary>The sum of this and <paramref name="other"/>.</summary>
    public McpCallCounts Add(McpCallCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new()
        {
            Calls = Calls + other.Calls,
            FailedConnection = FailedConnection + other.FailedConnection,
            FailedAuth = FailedAuth + other.FailedAuth,
            FailedOther = FailedOther + other.FailedOther,
            FailedUnknown = FailedUnknown + other.FailedUnknown
        };
    }
}

/// <summary>Values of the unavailable-servers maps: why an agent could not use an MCP server.</summary>
public static class McpServerStatuses
{
    /// <summary>The server did not start or could not be reached.</summary>
    public const string Failed = "failed";

    /// <summary>The server asked for credentials the agent did not have.</summary>
    public const string NeedsAuth = "needs-auth";
}

/// <summary>Keys of the tool-call maps: <c>server/tool</c>, and the built-in web tools under <c>web</c>.</summary>
public static class McpToolKeys
{
    /// <summary>The server part used for the agent tool's built-in web tools.</summary>
    public const string WebServer = "web";
    public const string WebSearch = "web/search";
    public const string WebFetch = "web/fetch";

    public static string Of(string server, string tool) => $"{server}/{tool}";

    /// <summary>The server part of a key ("sonarqube/get_issues" → "sonarqube").</summary>
    public static string ServerOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }
}
