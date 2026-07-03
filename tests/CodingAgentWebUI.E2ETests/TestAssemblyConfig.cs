using Xunit;

// Test parallelism is disabled because multiple WebApplicationFactory instances
// set process-global environment variables (Database__Host, WorkDistribution__Mode)
// that determine which code path Program.cs takes. With parallel execution, one
// factory's env vars bleed into another's host build.
//
// Performance note: This adds ~3-5 minutes vs parallel. Acceptable tradeoff for
// correctness in a Docker-only test environment. CI runs with --filter can target
// specific test classes for faster feedback.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
