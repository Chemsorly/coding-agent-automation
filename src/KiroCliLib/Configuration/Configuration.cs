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
}
