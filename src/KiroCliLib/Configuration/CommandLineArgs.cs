using Serilog.Events;

namespace KiroCliLib.Configuration;

/// <summary>
/// Command-line arguments for Kiro CLI integration.
/// </summary>
public class CommandLineArgs
{
    public string? Prompt { get; set; }
    public string? WorkspaceDirectory { get; set; }
    public string? AgentName { get; set; }
    public TimeSpan? Timeout { get; set; }
    public LogEventLevel? LogLevel { get; set; }
    public string? ConfigPath { get; set; }
    public string? TestScenario { get; set; }
}
