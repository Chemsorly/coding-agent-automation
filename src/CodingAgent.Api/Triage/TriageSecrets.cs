using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Triage;

/// <summary>
/// The secret values text about a project's triage may contain: the project's secrets, the secrets of every
/// repository of the project, and the credentials of the MCP servers a triage run can get (the project's and the
/// agent profiles'). Text posted to a tracker is masked with all of them, on the raw value, before it is
/// sanitized.
/// </summary>
internal static class TriageSecrets
{
    public static async Task<IReadOnlyCollection<KeyValuePair<string, string>>> LoadAsync(
        IConfigurationStore config, string projectId, CancellationToken ct)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string source, IEnumerable<KeyValuePair<string, string>>? values)
        {
            foreach (var (key, value) in values ?? [])
                secrets[$"{source}:{key}"] = value;
        }

        var project = await config.GetProjectByIdAsync(projectId, ct);
        Add("project", project?.Secrets);
        Add("project-mcp", TriageSecretMasking.McpSecrets(project?.McpServers));

        foreach (var template in await config.LoadTemplatesForProjectAsync(projectId, ct))
        {
            var repo = await config.GetProviderConfigByIdAsync(template.RepoProviderId, ProviderKind.Repository, ct);
            Add($"repo:{template.Id}", repo?.Secrets);
        }

        foreach (var profile in await config.LoadAgentProfilesAsync(ct))
            Add($"profile-mcp:{profile.Id}", TriageSecretMasking.McpSecrets(profile.McpServers));

        return secrets;
    }
}
