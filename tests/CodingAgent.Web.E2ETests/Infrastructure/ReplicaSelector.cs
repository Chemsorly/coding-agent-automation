namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Selects which replica of a <see cref="MultiReplicaE2EFixture"/> receives a request.
/// Used by <see cref="MultiReplicaE2EFixture.CompleteLikeProductionCrossReplicaAsync"/> to
/// choose which API replica handles the HTTP POST during a two-channel completion sequence.
/// </summary>
public enum ReplicaSelector
{
    /// <summary>Target <see cref="MultiReplicaE2EFixture.Replica1"/> / <see cref="MultiReplicaE2EFixture.AgentHubUrl1"/>.</summary>
    Replica1 = 1,

    /// <summary>Target <see cref="MultiReplicaE2EFixture.Replica2"/> / <see cref="MultiReplicaE2EFixture.AgentHubUrl2"/>.</summary>
    Replica2 = 2,
}
