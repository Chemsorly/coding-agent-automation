using Serilog.Events;

namespace CodingAgentWebUI.Infrastructure.Telemetry;

public static class LogLevelParser
{
    public static LogEventLevel Parse(string? value, LogEventLevel defaultLevel)
    {
        return value?.ToLowerInvariant() switch
        {
            "debug" or "dbg" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "verbose" or "trace" => LogEventLevel.Verbose,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" or "err" => LogEventLevel.Error,
            _ => defaultLevel
        };
    }
}