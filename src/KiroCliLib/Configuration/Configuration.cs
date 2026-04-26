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
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public LogEventLevel LogLevel { get; init; } = LogEventLevel.Information;
}
