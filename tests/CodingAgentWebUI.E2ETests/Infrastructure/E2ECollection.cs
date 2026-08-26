namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shares one <see cref="E2EFixture"/> across every E2E test class.
///
/// <para>
/// The fixture is expensive: it starts the Pipeline API and the Blazor host on real Kestrel ports,
/// builds two DI containers, and launches Chromium. As an <c>IClassFixture</c> that happened once
/// per test class — 31 times — and it showed up as pure overhead. A measured full run spent
/// 11m17s inside tests and 18m33s on the clock; the missing seven minutes were almost entirely
/// hosts being started and torn down between classes.
/// </para>
///
/// <para>
/// Sharing is safe because nothing about isolation depended on the fixture being per-class. Tests
/// already run one at a time (<c>DisableTestParallelization</c>, because the factories mutate
/// process-global environment variables), and every test already calls
/// <see cref="E2EFixture.ResetAllAsync"/> in its <c>InitializeAsync</c> — which clears the config
/// store, both hosts' registries and run state, the fake cluster, the in-memory database, the job
/// controller's in-flight set, and the pipeline loop. That reset is what has been providing
/// isolation between tests within a class all along; it provides exactly as much between classes.
/// </para>
///
/// <para>
/// The practical consequence for a new test class: declare <c>[Collection(E2ECollection.Name)]</c>
/// rather than <c>IClassFixture&lt;E2EFixture&gt;</c>, and never assume a freshly built host —
/// assume the reset.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EFixture>
{
    public const string Name = "E2E";

    // Marker type only: xUnit never constructs it.
}
