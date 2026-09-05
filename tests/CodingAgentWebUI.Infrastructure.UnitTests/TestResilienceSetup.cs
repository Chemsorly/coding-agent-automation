using System.Runtime.CompilerServices;
using CodingAgentWebUI.Infrastructure.Resilience;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Assembly-wide test setup: shrinks the resilience pipelines' base retry backoff to ~0 so tests that
/// exercise retry BEHAVIOUR (retry counts, exception mapping, exhaustion) don't wait real exponential
/// backoff (1–5 s per retry). No test asserts wall-clock backoff duration, so this is behaviour-preserving.
/// Runs once when the test assembly loads, before any test — never mutated afterwards, so it is safe for
/// xUnit's parallel test collections.
/// </summary>
internal static class TestResilienceSetup
{
    [ModuleInitializer]
    internal static void Init()
        => ResiliencePipelineFactory.TestRetryDelayOverride = TimeSpan.FromMilliseconds(1);
}
