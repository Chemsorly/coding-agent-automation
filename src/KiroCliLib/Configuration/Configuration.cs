using Serilog.Events;

namespace KiroCliLib.Configuration;

/// <summary>
/// Configuration settings for Kiro CLI integration.
/// </summary>
public class Configuration
{
    public string KiroCliPath { get; init; } = "/root/.local/bin/kiro-cli";
    public bool UseWsl { get; init; } = OperatingSystem.IsWindows();
    public string WorkspaceDirectory { get; init; } = "./workspace";
    public TimeSpan Timeout { get; init; } = KiroCliConstants.DefaultTimeout;
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// Passed as <c>--model</c> on every chat run unless blank or <c>auto</c>. A session flag outranks
    /// the user and workspace <c>cli.json</c>, so a repository's own Kiro settings cannot override it.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>Passed as <c>--agent</c> on every chat run unless blank.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Passed as <c>--effort</c> on every chat run unless blank. kiro-cli 2.29 accepts low, medium, high
    /// and max; for a model without effort support, or another level, it warns on stderr and runs with
    /// the model's default. The <c>chat.modelDefaults</c> effort in <c>cli.json</c> is not applied to
    /// headless runs, so the flag is the only way to set it.
    /// </summary>
    public string? Effort { get; init; }
}
