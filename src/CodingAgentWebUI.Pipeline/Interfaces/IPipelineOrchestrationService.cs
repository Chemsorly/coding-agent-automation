namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface exposing the primary operational method of the orchestration service.
/// Consumers that need to trigger pipeline cancellation depend on this interface
/// rather than the full concrete class.
/// <para>
/// Implementations:
/// <list type="bullet">
///   <item><description><see cref="Services.PipelineOrchestrationService"/> — production implementation</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Intentionally does NOT extend <see cref="IOrchestrationShutdownAction"/> — the cancel path
/// (normal operation) and the rolling-update handoff path remain independent narrow interfaces.
/// </remarks>
public interface IPipelineOrchestrationService
{
    /// <summary>
    /// Cancels the active pipeline run, swapping the issue label to <c>agent:cancelled</c>
    /// and delegating state transitions to the lifecycle service.
    /// </summary>
    Task CancelPipelineAsync();
}
