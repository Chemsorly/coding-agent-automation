namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Shares one <see cref="MultiReplicaE2EFixture"/> across all multi-replica test classes.
///
/// <para>
/// Kept separate from <see cref="E2ECollection"/> because the two fixtures have different
/// topologies: <see cref="E2EFixture"/> starts the full Blazor + API stack with Playwright;
/// <see cref="MultiReplicaE2EFixture"/> starts two API-only hosts sharing a
/// <see cref="CodingAgentWebUI.TestUtilities.FakeRedisStore"/>. Merging them would add the Blazor
/// host overhead to every multi-replica test and pollute the shared Kestrel address space.
/// </para>
///
/// <para>
/// Tests declare <c>[Collection(MultiReplicaE2ECollection.Name)]</c> and receive the fixture via
/// constructor injection. Per-test isolation is provided by
/// <see cref="MultiReplicaTestBase.InitializeAsync"/>, which calls
/// <see cref="MultiReplicaE2EFixture.ResetAll"/>.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MultiReplicaE2ECollection : ICollectionFixture<MultiReplicaE2EFixture>
{
    public const string Name = "MultiReplicaE2E";

    // Marker type only: xUnit never constructs it.
}
