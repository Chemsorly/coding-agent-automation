namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Full abstraction for multi-run tracking, combining registry, output streaming, and
/// activity query capabilities. All 27 production consumers continue to compile unchanged
/// via interface inheritance.
/// </summary>
/// <remarks>
/// The three role interfaces that compose this service each document their own semantic rules:
/// <list type="bullet">
///   <item><see cref="IActiveRunRegistry"/> — run lifecycle (add, replace, remove, get)</item>
///   <item><see cref="IRunOutputStream"/> — output backlog (append, read)</item>
///   <item><see cref="IRunActivityQuery"/> — cross-process queries (issue check, branches)</item>
/// </list>
/// </remarks>
public interface IOrchestratorRunService : IActiveRunRegistry, IRunOutputStream, IRunActivityQuery
{
}
