using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Serializes all BackgroundService timing tests so they do not compete for thread-pool
/// threads concurrently. PeriodicTimer with a 1ms interval is sensitive to thread
/// starvation — running multiple services in parallel on a loaded CI host can cause the
/// first WaitForNextTickAsync to fire after StopAsync cancels the token, making the test
/// appear as if the API was never called.
/// </summary>
[CollectionDefinition("SchedulerTiming")]
public class TimingTestCollection;
