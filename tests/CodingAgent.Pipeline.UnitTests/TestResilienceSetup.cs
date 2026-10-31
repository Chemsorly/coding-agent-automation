using System.Runtime.CompilerServices;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Assembly-wide test setup: shrinks fixed retry and backoff waits to ~0 so tests that exercise the
/// BEHAVIOUR around them (retry counts, recovery, exhaustion) don't wait them out for real. No test
/// asserts their wall-clock duration, so this is behaviour-preserving. Runs once when the test assembly
/// loads, before any test — never mutated afterwards, so it is safe for xUnit's parallel test collections.
/// </summary>
internal static class TestResilienceSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        ResiliencePipelineFactory.TestRetryDelayOverride = TimeSpan.FromMilliseconds(1);
        AgentLabelOperations.TestRetryDelayOverride = TimeSpan.FromMilliseconds(1);
        PipelineLoopService.TestWaitOverride = TimeSpan.FromMilliseconds(10);
    }
}
