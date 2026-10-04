namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Values for the Claude Code agent provider's <c>authMode</c> setting.
/// </summary>
public static class ClaudeCodeAuthModes
{
    /// <summary>Use the API key when one is configured, otherwise the subscription token.</summary>
    public const string Auto = "auto";

    /// <summary>Use the Anthropic API key (billed per token).</summary>
    public const string ApiKey = "apiKey";

    /// <summary>Use the long-lived subscription token from <c>claude setup-token</c>.</summary>
    public const string Subscription = "subscription";

    public static readonly string[] All = [Auto, ApiKey, Subscription];

    /// <summary>Maps any input to one of <see cref="All"/>; unknown or empty values become <see cref="Auto"/>.</summary>
    public static string Normalize(string? value) =>
        All.FirstOrDefault(m => m.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Auto;
}

/// <summary>A Claude model the Claude Code CLI accepts through <c>--model</c>.</summary>
/// <param name="ModelId">Full model ID, or an alias that always points to the latest model of a family.</param>
/// <param name="Description">Short label for the model picker.</param>
public sealed record ClaudeCodeModel(string ModelId, string Description);

/// <summary>
/// Models offered in the agent provider form for Claude Code. The CLI has no command that lists
/// models, so this list is maintained by hand. Any other model ID can still be typed in.
/// </summary>
public static class ClaudeCodeModels
{
    public static readonly IReadOnlyList<ClaudeCodeModel> All =
    [
        new("claude-fable-5-1", "Claude Fable 5.1 — most capable"),
        new("claude-fable-5", "Claude Fable 5"),
        new("claude-opus-5-5", "Claude Opus 5.5"),
        new("claude-opus-5", "Claude Opus 5"),
        new("claude-opus-4-8", "Claude Opus 4.8"),
        new("claude-opus-4-7", "Claude Opus 4.7"),
        new("claude-opus-4-6", "Claude Opus 4.6"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-sonnet-5", "Claude Sonnet 5"),
        new("claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new("claude-haiku-4-5", "Claude Haiku 4.5 — fastest"),
        new("fable", "Alias: latest Fable"),
        new("opus", "Alias: latest Opus"),
        new("sonnet", "Alias: latest Sonnet"),
        new("haiku", "Alias: latest Haiku"),
    ];
}
