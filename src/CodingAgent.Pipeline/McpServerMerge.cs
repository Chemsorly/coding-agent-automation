using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline;

/// <summary>
/// Stateless utility for merging profile-level and project-level MCP server configurations.
/// Project servers override profile servers with the same Name (case-insensitive);
/// new project server names are appended. Null or empty project servers = passthrough.
/// </summary>
public static class McpServerMerge
{
    /// <summary>
    /// Merges <paramref name="profileServers"/> and <paramref name="projectServers"/> into a
    /// single list using project-wins-on-collision semantics.
    /// <para>
    /// When <paramref name="projectServers"/> is <c>null</c> or empty, returns
    /// <paramref name="profileServers"/> unchanged (short-circuit passthrough).
    /// Otherwise, seeds a case-insensitive dictionary from the profile list, then overwrites
    /// matching entries with project entries and appends any new names.
    /// </para>
    /// </summary>
    /// <param name="profileServers">The agent-profile-level MCP server list (base config).</param>
    /// <param name="projectServers">The project-level MCP server list (overrides), or <c>null</c>.</param>
    /// <returns>
    /// The merged <see cref="IReadOnlyList{T}"/> of <see cref="McpServerConfig"/> entries.
    /// </returns>
    public static IReadOnlyList<McpServerConfig> Merge(
        IReadOnlyList<McpServerConfig> profileServers,
        IReadOnlyList<McpServerConfig>? projectServers)
    {
        // TODO: profileServers is not null-guarded. Passing null reaches profileServers.ToDictionary(...)
        // and throws NullReferenceException. Add ArgumentNullException.ThrowIfNull(profileServers) or
        // a null-coalescing fallback (profileServers ?? []) to make the public contract explicit.
        if (projectServers is null or { Count: 0 })
            return profileServers;

        var merged = profileServers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var ps in projectServers)
            merged[ps.Name] = ps;

        return merged.Values.ToList();
    }
}
