using Serilog.Events;

namespace KiroCliPoc.Configuration;

/// <summary>
/// Command-line arguments for the Kiro CLI PoC application.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS
/// </remarks>
public class CommandLineArgs
{
    /// <summary>
    /// Gets or sets the prompt to send to Kiro CLI.
    /// </summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Gets or sets the workspace directory.
    /// </summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>
    /// Gets or sets the Kiro agent name.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Gets or sets the execution timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets the log level.
    /// </summary>
    public LogEventLevel? LogLevel { get; set; }

    /// <summary>
    /// Gets or sets the path to the configuration file.
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Gets or sets the test scenario name to execute.
    /// </summary>
    public string? TestScenario { get; set; }
}
