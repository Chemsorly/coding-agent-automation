using Serilog.Events;

namespace KiroCliPoc.Configuration;

/// <summary>
/// Configuration settings for the Kiro CLI PoC application.
/// </summary>
public class Configuration
{
    /// <summary>
    /// Gets or initializes the path to the Kiro CLI executable.
    /// Default is the full path in WSL: /root/.local/bin/kiro-cli
    /// </summary>
    public string KiroCliPath { get; init; } = "/root/.local/bin/kiro-cli";

    /// <summary>
    /// Gets or initializes a value indicating whether to use WSL to invoke Kiro CLI.
    /// Default is true on Windows, false on Linux/Mac.
    /// </summary>
    public bool UseWsl { get; init; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Gets or initializes the workspace directory where Kiro CLI will execute.
    /// </summary>
    public string WorkspaceDirectory { get; init; } = "./workspace";

    /// <summary>
    /// Gets or initializes the Kiro agent name to use for code generation.
    /// </summary>
    public string AgentName { get; init; } = "feature-developer";

    /// <summary>
    /// Gets or initializes the execution timeout for Kiro CLI.
    /// Default is 30 minutes.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or initializes the log level for Serilog.
    /// </summary>
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// Gets or initializes the optional log file path.
    /// If null, logs only to console.
    /// </summary>
    public string? LogFilePath { get; init; }
}
