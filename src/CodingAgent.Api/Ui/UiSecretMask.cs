using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 D4: secret values never leave the API on the UI surface, not even for an admin. The
/// fixed list here covers every project secret and every MCP server env value and header (headers
/// carry tokens for HTTP servers). Other values are returned in clear.
///
/// <para>
/// Reading replaces each secret value with <see cref="Mask"/>. Writing <see cref="Mask"/> back keeps
/// the stored value under the same key (of the same MCP server, matched by name), so a form can be
/// saved unchanged. A key that is left out is removed. A <see cref="Mask"/> with no stored value
/// behind it is refused rather than stored as a literal.
/// </para>
/// </summary>
internal static class UiSecretMask
{
    public const string Mask = "****";

    public static PipelineProject Redact(PipelineProject project) => project with
    {
        Secrets = project.Secrets?.ToDictionary(s => s.Key, _ => Mask),
        McpServers = project.McpServers?.Select(Redact).ToList(),
    };

    public static AgentProfile Redact(AgentProfile profile) => profile with
    {
        McpServers = profile.McpServers.Select(Redact).ToList(),
    };

    private static McpServerConfig Redact(McpServerConfig server) => server with
    {
        Env = server.Env.ToDictionary(e => e.Key, _ => Mask),
        Headers = server.Headers.ToDictionary(h => h.Key, _ => Mask),
    };

    /// <summary>The project to store: masked values replaced by the stored ones. Null with a reason when a mask has nothing behind it.</summary>
    public static (PipelineProject? Restored, string? Error) Restore(PipelineProject incoming, PipelineProject? stored)
    {
        var errors = new List<string>();
        var restored = incoming with
        {
            Secrets = incoming.Secrets is null ? null : RestoreValues(incoming.Secrets, stored?.Secrets, "project secret", errors),
            McpServers = incoming.McpServers?.Select(s => Restore(s, stored?.McpServers, errors)).ToList(),
        };
        return errors.Count == 0 ? (restored, null) : (null, string.Join(" ", errors));
    }

    /// <summary>The profile to store: masked values replaced by the stored ones. Null with a reason when a mask has nothing behind it.</summary>
    public static (AgentProfile? Restored, string? Error) Restore(AgentProfile incoming, AgentProfile? stored)
    {
        var errors = new List<string>();
        var restored = incoming with
        {
            McpServers = incoming.McpServers.Select(s => Restore(s, stored?.McpServers, errors)).ToList(),
        };
        return errors.Count == 0 ? (restored, null) : (null, string.Join(" ", errors));
    }

    private static McpServerConfig Restore(McpServerConfig incoming, IReadOnlyList<McpServerConfig>? storedServers, List<string> errors)
    {
        var stored = storedServers?.FirstOrDefault(s => string.Equals(s.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
        return incoming with
        {
            Env = RestoreValues(incoming.Env, stored?.Env, $"env value of MCP server '{incoming.Name}'", errors),
            Headers = RestoreValues(incoming.Headers, stored?.Headers, $"header of MCP server '{incoming.Name}'", errors),
        };
    }

    private static Dictionary<string, string> RestoreValues(
        IReadOnlyDictionary<string, string> incoming,
        IReadOnlyDictionary<string, string>? stored,
        string what,
        List<string> errors)
    {
        var result = new Dictionary<string, string>(incoming.Count);
        foreach (var (key, value) in incoming)
        {
            if (value != Mask)
                result[key] = value;
            else if (stored is not null && stored.TryGetValue(key, out var storedValue))
                result[key] = storedValue;
            else
                errors.Add($"The {what} '{key}' is masked but no value is stored for it; enter the value.");
        }
        return result;
    }
}
