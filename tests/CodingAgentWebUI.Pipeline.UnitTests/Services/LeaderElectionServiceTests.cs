using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class LeaderElectionServiceTests
{
    private static IOptions<LeaderElectionOptions> CreateOptions(Action<LeaderElectionOptions>? configure = null)
    {
        var opts = new LeaderElectionOptions();
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    [Fact]
    public void IsLeader_DefaultsToFalse()
    {
        var sut = new LeaderElectionService(CreateOptions());
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void LeaderToken_WhenNotStarted_IsCancelled()
    {
        var sut = new LeaderElectionService(CreateOptions());
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WithoutKubeClient_StaysNonLeader()
    {
        var sut = new LeaderElectionService(CreateOptions());

        await sut.StartAsync(CancellationToken.None);

        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithoutKubeClient_DoesNotThrow()
    {
        var sut = new LeaderElectionService(CreateOptions());

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WithFailOnNonK8s_ThrowsInvalidOperationException()
    {
        var sut = new LeaderElectionService(
            CreateOptions(o => o.FailOnNonKubernetesEnvironment = true));

        var act = () => sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Kubernetes environment*");
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var sut = new LeaderElectionService(CreateOptions());

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_WithoutKubeClient_LeaderTokenRemainsCancelled()
    {
        var sut = new LeaderElectionService(CreateOptions());

        await sut.StartAsync(CancellationToken.None);

        // No k8s client → service doesn't even start election loop → token stays cancelled
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var opts = new LeaderElectionOptions();

        opts.LeaseName.Should().Be("caa-leader");
        opts.Namespace.Should().BeNull();
        opts.LeaseDuration.Should().Be(TimeSpan.FromSeconds(15));
        opts.RenewDeadline.Should().Be(TimeSpan.FromSeconds(10));
        opts.RetryPeriod.Should().Be(TimeSpan.FromSeconds(2));
        opts.Identity.Should().BeNull();
        opts.FailOnNonKubernetesEnvironment.Should().BeFalse();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var sut = new LeaderElectionService(CreateOptions());

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_AfterStart_DoesNotThrow()
    {
        var sut = new LeaderElectionService(CreateOptions());
        await sut.StartAsync(CancellationToken.None);

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }
}
