namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Writes Kiro CLI model and effort settings to <c>~/.kiro/settings/cli.json</c>.
/// Used by both work-item pods (via <c>KiroCliAgentProvider</c>) and chat pods
/// (via <c>AgentConnectionLifecycle</c>) so all code paths share the same file-write logic.
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
                if (!ValidEffortValues.Contains(effort))
                {
                    Serilog.Log.Warning("KiroCliSettingsWriter: invalid effort value rejected: {Effort}", effort);
                }
                else
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

    /// <summary>
    /// Valid effort level strings accepted by the Kiro CLI.
    /// Covers all <see cref="CodingAgentWebUI.Pipeline.Models.AgentEffortLevel"/> enum values
    /// that produce a non-null <c>ToCliValue()</c> result.
    /// </summary>
    // TODO: Change to IReadOnlySet<string> (or FrozenSet<string>) to prevent callers from mutating
    // the shared set at runtime (e.g. ValidEffortValues.Add / Clear). HashSet<string> readonly only
    // prevents field reassignment, not collection mutation. See review finding [WARNING] DotNetSpecialist.
    // TODO: Verify that "xhigh" and "max" are accepted by the Kiro CLI binary deployed in chat pods.
    // The set was expanded from {"low","medium","high"} to include these two values to match
    // AgentEffortLevel enum members, but ChatJobDispatcher now forwards them to CHAT_EFFORT env-var
    // without CLI contract evidence. If unsupported, remove them or gate behind a feature flag.
    // See review finding [WARNING] Correctness.
    public static readonly HashSet<string> ValidEffortValues =
        new(["low", "medium", "high", "xhigh", "max"], StringComparer.OrdinalIgnoreCase);
}
