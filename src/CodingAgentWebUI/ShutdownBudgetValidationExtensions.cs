using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for validating the shutdown budget at application startup.
/// Warns if the configured drain delay + shutdown timeout exceeds the host ShutdownTimeout budget.
/// </summary>
internal static class ShutdownBudgetValidationExtensions
{
    /// <summary>
    /// Validates that the shutdown budget (drain delay + ShutdownService timeout + buffer)
    /// does not exceed the configured HostOptions.ShutdownTimeout.
    /// Logs a warning if the budget is exceeded — ShutdownService may be force-killed.
    /// </summary>
    /// <remarks>
    /// Safe to call at any point after Build(). No ordering dependencies on other startup methods.
    /// </remarks>
    public static WebApplication ValidateShutdownBudget(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var drainDelay = ReadinessDrainService.ResolveDrainDelay();
        const int shutdownServiceTimeout = 15; // ShutdownService default
        const int hostShutdownTimeout = 40;    // HostOptions.ShutdownTimeout value set above
        const int requiredBuffer = 5;          // Minimum buffer for host finalization
        var totalRequired = drainDelay.TotalSeconds + shutdownServiceTimeout + requiredBuffer;
        if (totalRequired > hostShutdownTimeout)
        {
            Log.Warning(
                "Shutdown budget exceeded: drain ({DrainDelay}s) + ShutdownService ({ShutdownTimeout}s) + buffer ({Buffer}s) = {Total}s > HostShutdownTimeout ({HostTimeout}s). " +
                "ShutdownService may be force-killed before completing. Reduce READINESS_DRAIN_DELAY_SECONDS or increase HostOptions.ShutdownTimeout.",
                drainDelay.TotalSeconds, shutdownServiceTimeout, requiredBuffer, totalRequired, hostShutdownTimeout);
        }

        return app;
    }
}
