namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Future E2E coverage for Scheduler-specific scenarios (Spec 047 placeholder).
///
/// When added, this factory would host CodingAgentWebUI.Scheduler with in-memory
/// configuration and a FakeKubernetesJobClient, similar to ApiE2EWebApplicationFactory.
/// It would expose the PipelineLoopService singleton so tests can drive start/stop
/// without a real Pipeline API connection.
///
/// Blocked by: Scheduler needs a dedicated E2E harness with a fake IPipelineApiConfigClient
/// and a fake ISchedulerApiClient pointing to the in-process Scheduler.
/// </summary>
internal sealed class SchedulerE2EWebApplicationFactory
{
    // TODO(Spec 048 or later): implement when Scheduler E2E coverage is needed.
}
