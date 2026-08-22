using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Serilog.Enrichers.Span;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Provides Serilog configuration for the agent worker.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
internal static class AgentSerilogConfiguration
{
    internal static Serilog.ILogger CreateAgentLogger(AgentId agentId)
    {
        var logLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel),
            Serilog.Events.LogEventLevel.Information);

        return new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            // Suppress noisy ASP.NET Core request logging (health checks every 10s)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            // Suppress noisy HttpClient logging (OpenCode health monitor polls every 5s)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            // Suppress HttpClientFactory handler lifecycle logging (cleanup cycle every 10s)
            .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .Enrich.WithProperty("AgentId", agentId)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{AgentId}] {Message:lj}{NewLine}{Exception}")
            .WriteToOtlpIfConfigured(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-worker")
            .CreateLogger();
    }
}
