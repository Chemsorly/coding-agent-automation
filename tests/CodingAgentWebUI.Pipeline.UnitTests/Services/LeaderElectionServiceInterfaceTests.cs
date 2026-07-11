using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that <see cref="LeaderElectionService"/> correctly implements <see cref="ILeaderElectionService"/>.
/// </summary>
public sealed class LeaderElectionServiceInterfaceTests
{
    [Fact]
    public void ImplementsILeaderElectionService()
    {
        var options = Options.Create(new LeaderElectionOptions());
        var svc = new LeaderElectionService(options, kubeClient: null);

        ILeaderElectionService iface = svc;
        iface.Should().NotBeNull();
        iface.IsLeader.Should().BeFalse();
        iface.LeaderToken.IsCancellationRequested.Should().BeTrue();
        svc.Dispose();
    }

    [Fact]
    public async Task NonKubernetesMode_StaysNonLeader()
    {
        var options = Options.Create(new LeaderElectionOptions());
        var svc = new LeaderElectionService(options, kubeClient: null);

        await svc.StartAsync(CancellationToken.None);

        ILeaderElectionService iface = svc;
        iface.IsLeader.Should().BeFalse();
        iface.LeaderToken.IsCancellationRequested.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);
        svc.Dispose();
    }
}
