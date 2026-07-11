using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ILeaderElectionServiceTests
{
    [Fact]
    public void LeaderElectionService_ImplementsInterface()
    {
        var sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        sut.Should().BeAssignableTo<ILeaderElectionService>();
    }

    [Fact]
    public void PostgresLeaderElectionService_ImplementsInterface()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            new PostgresLeaderElectionOptions());
        sut.Should().BeAssignableTo<ILeaderElectionService>();
        sut.Dispose();
    }

    [Fact]
    public void Interface_ExposesIsLeader()
    {
        ILeaderElectionService sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void Interface_ExposesLeaderToken()
    {
        ILeaderElectionService sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    // TODO: These tests only verify event subscription compiles and doesn't throw, not that events actually fire. Consider adding behavioral tests.
    [Fact]
    public void Interface_ExposesOnStartedLeading()
    {
        ILeaderElectionService sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var fired = false;
        sut.OnStartedLeading += () => fired = true;
        fired.Should().BeFalse();
    }

    [Fact]
    public void Interface_ExposesOnStoppedLeading()
    {
        ILeaderElectionService sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        var fired = false;
        sut.OnStoppedLeading += () => fired = true;
        fired.Should().BeFalse();
    }
}
