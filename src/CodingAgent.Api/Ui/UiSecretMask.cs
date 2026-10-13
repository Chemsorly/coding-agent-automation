using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 D4: secret values never leave the API on the UI surface, not even for an admin. The
/// fixed list here covers every project secret, every MCP server env value and header (headers
/// carry tokens for HTTP servers), every provider secret and the provider credential settings
/// (<see cref="ProviderSecretSettings"/>). Other values are returned in clear.
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

    /// <summary>Provider Settings keys that hold credentials (D4). Every <see cref="ProviderConfig.Secrets"/> value is one too.</summary>
    internal static readonly IReadOnlySet<string> ProviderSecretSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderSettingKeys.PrivateKeyBase64,
        ProviderSettingKeys.Token,
        ProviderSettingKeys.AccessToken,
    };

    /// <summary>Provider Settings keys that say where credentials are sent.</summary>
    private static readonly string[] ProviderUrlSettings = [ProviderSettingKeys.ApiUrl, ProviderSettingKeys.BaseUrl];

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

    public static ProviderConfig Redact(ProviderConfig config) => Copy(config,
        config.Settings.ToDictionary(s => s.Key, s => ProviderSecretSettings.Contains(s.Key) ? Mask : s.Value),
        config.Secrets?.ToDictionary(s => s.Key, _ => Mask));

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

    /// <summary>
    /// The provider config to store or to run an operation with: masked values replaced by the stored
    /// ones. A stored credential is only ever used with its stored URL (D4): a draft that changes the
    /// API or base URL while leaving a credential masked is refused, so a stored key cannot be sent to
    /// a URL of the caller's choosing.
    /// </summary>
    public static (ProviderConfig? Restored, string? Error) Restore(ProviderConfig incoming, ProviderConfig? stored)
    {
        var errors = new List<string>();
        var settings = RestoreValues(incoming.Settings, stored?.Settings, "provider setting", errors);
        var secrets = incoming.Secrets is null ? null : RestoreValues(incoming.Secrets, stored?.Secrets, "provider secret", errors);

        var restoresACredential = incoming.Settings.Any(s => s.Value == Mask) || incoming.Secrets?.Any(s => s.Value == Mask) == true;
        if (errors.Count == 0 && restoresACredential && stored is not null && ProviderUrlSettings.Any(key => UrlChanged(key, incoming, stored)))
            errors.Add("The API URL changed: enter the credentials again. A stored credential is only used with its stored URL.");

        return errors.Count == 0 ? (Copy(incoming, settings, secrets), null) : (null, string.Join(" ", errors));
    }

    private static bool UrlChanged(string key, ProviderConfig incoming, ProviderConfig stored) =>
        !string.Equals(EffectiveUrl(key, incoming), EffectiveUrl(key, stored), StringComparison.Ordinal);

    /// <summary>
    /// The URL the provider calls: a left-out API URL is the provider's default, as the form fills it
    /// in, and a trailing slash does not change it.
    /// </summary>
    private static string EffectiveUrl(string key, ProviderConfig config)
    {
        var url = config.Settings.GetValueOrDefault(key)?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(url) || key != ProviderSettingKeys.ApiUrl)
            return url ?? "";
        return config.ProviderType switch
        {
            ProviderTypes.GitHub => ProviderSettingKeys.DefaultApiUrl,
            ProviderTypes.GitLab => ProviderSettingKeys.DefaultGitLabApiUrl,
            _ => "",
        };
    }

    /// <summary>ProviderConfig is a class with init-only properties, so a changed copy is built field by field.</summary>
    internal static ProviderConfig Copy(ProviderConfig config, Dictionary<string, string> settings, Dictionary<string, string>? secrets) => new()
    {
        BlacklistedPaths = config.BlacklistedPaths,
        DisplayName = config.DisplayName,
        Id = config.Id,
        Kind = config.Kind,
        ProviderType = config.ProviderType,
        RepositoryRole = config.RepositoryRole,
        RequiredLabels = config.RequiredLabels,
        Secrets = secrets,
        Settings = settings,
        SetupSteps = config.SetupSteps,
        SteeringContent = config.SteeringContent,
    };

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
