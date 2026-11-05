using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Web.TestUtilities;
using k8s.Models;
using Moq;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Dispatch;

/// <summary>Spec 057: the gate test doubles behave like the real gate where tests depend on it.</summary>
public sealed class AgentStartGateFakesTests
{
    [Fact]
    public async Task AllowAll_ShouldSerializeLeases_UntilTheFirstIsDisposed()
    {
        var gate = AgentStartGateFakes.AllowAll();
        var first = await gate.EnterAsync(CancellationToken.None);

        var second = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse("the first lease still holds the lock");

        await first.DisposeAsync();
        await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Entered.Should().Be(2);
        gate.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task Lease_DisposedTwice_ShouldCountOnce()
    {
        var gate = AgentStartGateFakes.AllowAll();
        var lease = await gate.EnterAsync(CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        gate.Disposed.Should().Be(1);
    }

    [Fact]
    public async Task AllowAll_WithAJobClient_ShouldReadTheHeldPvcsFromIt()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ListJobsAsync("test", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList
            {
                Items =
                [
                    new V1Job
                    {
                        Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string>() },
                        Spec = new V1JobSpec
                        {
                            Template = new V1PodTemplateSpec
                            {
                                Spec = new V1PodSpec
                                {
                                    Containers = [],
                                    Volumes = [new V1Volume { Name = "c", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-0" } }]
                                }
                            }
                        }
                    }
                ]
            });
        var gate = AgentStartGateFakes.AllowAll(client.Object, "test");

        await using var lease = await gate.EnterAsync(CancellationToken.None);

        lease.Jobs.HeldPvcs.Should().BeEquivalentTo(["pvc-0"]);
    }

    [Fact]
    public async Task AllowAll_WhenTheJobClientThrows_ShouldThrowAgentCountUnavailable_AndReleaseTheLock()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var gate = AgentStartGateFakes.AllowAll(client.Object);

        var act = () => gate.EnterAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AgentCountUnavailableException>();
        await act.Should().ThrowAsync<AgentCountUnavailableException>("the lock was released, so a second enter is not blocked");
    }

    [Fact]
    public async Task UnavailableAndLockTimeout_ShouldThrowFromEnter()
    {
        await FluentEnter(AgentStartGateFakes.Unavailable()).Should().ThrowAsync<AgentCountUnavailableException>();
        await FluentEnter(AgentStartGateFakes.LockTimeout()).Should().ThrowAsync<TimeoutException>();
    }

    private static Func<Task> FluentEnter(FakeAgentStartGate gate) => () => gate.EnterAsync(CancellationToken.None);
}
