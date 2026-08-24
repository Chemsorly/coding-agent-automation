namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for multi-replica tests. Provides per-test state reset and exposes the
/// shared <see cref="MultiReplicaE2EFixture"/> fixture.
/// </summary>
public abstract class MultiReplicaTestBase : IAsyncLifetime
{
    protected MultiReplicaE2EFixture Fixture { get; }

    protected MultiReplicaTestBase(MultiReplicaE2EFixture fixture)
    {
        Fixture = fixture;
    }

    public Task InitializeAsync()
    {
        Fixture.ResetAll();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
