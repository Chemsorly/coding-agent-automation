namespace CodingAgentWebUI.Agent;

/// <summary>
/// Writes Kiro CLI model and effort settings to <c>~/.kiro/settings/cli.json</c>.
/// Extracted from <c>KiroCliAgentProvider.ApplyCliSettingsAsync</c> so both work-item
/// pods (via <c>KiroCliAgentProvider</c>) and chat pods (via <c>AgentConnectionLifecycle</c>)
/// share the same file-write logic.
/// </summary>
public static partial class KiroCliSettingsWriter
{
    /// <summary>
    /// Persists model and effort settings to <c>~/.kiro/settings/cli.json</c>.
    /// Sets <c>chat.defaultModel</c> and, when effort is provided,
    /// <c>chat.modelDefaults.{model}.output_config.effort</c>.
    /// </summary>
    /// <param name="model">Model name to set. Must be non-empty and not "auto".</param>
    /// <param name="effort">Effort level string, e.g. "high" or "low". Null or empty → not written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="settingsPathOverride">Override path for testing; null uses the default user-profile path.</param>
    public static async Task ApplyAsync(string model, string? effort, CancellationToken ct,
        string? settingsPathOverride = null)
    {
        if (string.IsNullOrEmpty(model) || model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return;

        if (!ModelNamePattern().IsMatch(model))
        {
            Serilog.Log.Warning("KiroCliSettingsWriter: invalid model name rejected: {Model}", model);
            return;
        }

        var settingsPath = settingsPathOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kiro", "settings", "cli.json");

        try
        {
            var settingsDir = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(settingsDir);

            // Read existing settings or start fresh
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(settingsPath))
            {
                var existing = await File.ReadAllTextAsync(settingsPath, ct);
                root = System.Text.Json.Nodes.JsonNode.Parse(existing)?.AsObject()
                       ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }

            // Set chat.defaultModel
            root["chat.defaultModel"] = model;

            // Set chat.modelDefaults.{model}.output_config.effort (only when effort provided)
            if (!string.IsNullOrEmpty(effort))
            {
                var modelDefaults = root["chat.modelDefaults"]?.AsObject()
                                    ?? new System.Text.Json.Nodes.JsonObject();
                root["chat.modelDefaults"] = modelDefaults;

                var modelNode = modelDefaults[model]?.AsObject()
                                ?? new System.Text.Json.Nodes.JsonObject();
                modelDefaults[model] = modelNode;

                var outputConfig = modelNode["output_config"]?.AsObject()
                                   ?? new System.Text.Json.Nodes.JsonObject();
                modelNode["output_config"] = outputConfig;

                outputConfig["effort"] = effort;
            }

            var json = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(settingsPath, json, ct);

            Serilog.Log.Information("KiroCliSettingsWriter: persisted CLI settings (model={Model}, effort={Effort}) to {Path}",
                model, effort ?? "auto", settingsPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "KiroCliSettingsWriter: failed to persist CLI settings to {Path}", settingsPath);
        }
    }

    /// <summary>Pattern for valid model names: alphanumeric, dots, hyphens, underscores.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial System.Text.RegularExpressions.Regex ModelNamePattern();
}
