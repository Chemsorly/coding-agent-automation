using System.Runtime.InteropServices;
using CodingAgent.Pipeline;

namespace CodingAgent.Agent;

/// <summary>
/// Startup steps that keep the agent key away from the processes the worker starts, which run as the same user.
/// <c>Program.cs</c> calls them in a fixed order, before it builds the host.
/// </summary>
internal static class AgentProcessHardening
{
    /// <summary>Set to 0 in the claude and kiro images. The runtime reads it at startup, before any managed code.</summary>
    internal const string EnvDotnetEnableDiagnostics = "DOTNET_EnableDiagnostics";

    private const int PrSetDumpable = 4;

    /// <summary>
    /// Closes this process's <c>/proc/{pid}/environ</c>, <c>mem</c> and <c>fd</c> to other processes of the same
    /// user, and turns off crash dumps and debugger attach. Applies on Linux only; elsewhere it does nothing, so
    /// local development on Windows and macOS is unchanged.
    /// </summary>
    /// <returns><see langword="false"/> when the call failed.</returns>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "prctl is a P/Invoke that cannot be called safely in unit tests on Linux; verified at image level by the next issue.")]
    internal static bool MarkProcessNotDumpable()
    {
        if (!OperatingSystem.IsLinux())
            return true;

        return SetProcessControl(PrSetDumpable, 0, 0, 0, 0) == 0;
    }

    /// <summary>
    /// Removes the agent key and the diagnostics setting from this process's environment, so child processes do
    /// not inherit them and <c>IConfiguration</c> never sees the key.
    /// </summary>
    internal static void RemoveSecretsFromEnvironment()
    {
        Environment.SetEnvironmentVariable(AgentDefaults.EnvAgentApiKey, null);
        Environment.SetEnvironmentVariable(EnvDotnetEnableDiagnostics, null);
    }

    /// <summary>Linux <c>prctl(2)</c>; .NET has no managed equivalent.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054",
        Justification = "LibraryImport needs AllowUnsafeBlocks for the project; plain integers need no generated marshalling.")]
    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetProcessControl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
