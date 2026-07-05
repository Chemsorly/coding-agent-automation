using CodingAgentWebUI.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Validates that the configured shutdown budget (drain delay + shutdown service timeout + buffer)
/// does not exceed the host's <c>ShutdownTimeout</c>.
/// </summary>
internal static class ShutdownBudgetValidator
{
    /// <summary>
    /// Logs a warning if the total shutdown budget exceeds the host shutdown timeout,
    /// which would cause the ShutdownService to be force-killed before completing.
    /// </summary>
    public static void ValidateShutdownBudget(int hostShutdownTimeoutSeconds = 40)
    {
        var drainDelay = ReadinessDrainService.ResolveDrainDelay();
        const int shutdownServiceTimeout = 15; // ShutdownService default
        const int requiredBuffer = 5;          // Minimum buffer for host finalization
        var totalRequired = drainDelay.TotalSeconds + shutdownServiceTimeout + requiredBuffer;
        if (totalRequired > hostShutdownTimeoutSeconds)
        {
            Log.Warning(
                "Shutdown budget exceeded: drain ({DrainDelay}s) + ShutdownService ({ShutdownTimeout}s) + buffer ({Buffer}s) = {Total}s > HostShutdownTimeout ({HostTimeout}s). " +
                "ShutdownService may be force-killed before completing. Reduce READINESS_DRAIN_DELAY_SECONDS or increase HostOptions.ShutdownTimeout.",
                drainDelay.TotalSeconds, shutdownServiceTimeout, requiredBuffer, totalRequired, hostShutdownTimeoutSeconds);
        }
    }
}
