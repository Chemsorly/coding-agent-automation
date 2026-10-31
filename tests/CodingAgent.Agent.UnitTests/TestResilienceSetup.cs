using System.Runtime.CompilerServices;
using CodingAgent.Infrastructure.Resilience;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Assembly-wide test setup: shrinks the resilience pipelines' base retry backoff and
/// <see cref="OrchestratorProxy"/>'s token-vend retry wait to ~0, so tests that exercise retry BEHAVIOUR
/// (retry counts, exhaustion) or vend a token against an unstarted hub don't wait real backoff.
/// No test asserts wall-clock backoff duration, so this is behaviour-preserving.
/// Runs once when the test assembly loads, before any test — never mutated afterwards, so it is safe for
/// xUnit's parallel test collections.
/// </summary>
internal static class TestResilienceSetup
{
    [ModuleInitializer]
    internal static void Init()
        => ResiliencePipelineFactory.TestRetryDelayOverride = TimeSpan.FromMilliseconds(1);
}
