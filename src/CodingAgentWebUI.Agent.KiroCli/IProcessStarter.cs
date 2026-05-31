using System.Diagnostics;

namespace CodingAgentWebUI.Agent.KiroCli;

/// <summary>Abstraction over Process.Start for testability.</summary>
internal interface IProcessStarter
{
    Process? Start(ProcessStartInfo psi);
}

internal sealed class DefaultProcessStarter : IProcessStarter
{
    public Process? Start(ProcessStartInfo psi) => Process.Start(psi);
}
