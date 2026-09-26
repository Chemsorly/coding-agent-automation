using System.Net;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using k8s.Autorest;
using k8s.Models;

namespace CodingAgent.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="AgentJobKeySecret"/>, the per-Job agent key shared by every dispatcher
/// (work item, chat, model fetch) — Spec 043 Req 8a.
/// </summary>
public sealed class AgentJobKeySecretTests
{
    private const string Namespace = "caa";
    private const string JobName = "caa-chat-1a2b3c4d";
    private const string MasterKey = "master-key";

    [Fact]
    public async Task CreateForJobAsync_StoresTheJobsDerivedKey_OwnedByTheJob()
    {
        var client = new Mock<IKubernetesJobClient>();
        V1Secret? created = null;
        client.Setup(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), Namespace, It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => created = s)
            .Returns(Task.CompletedTask);

        await AgentJobKeySecret.CreateForJobAsync(client.Object, Namespace, JobName, "job-uid", MasterKey, CancellationToken.None);

        created.Should().NotBeNull();
        created!.Metadata.Name.Should().Be($"caa-key-{JobName}");
        created.StringData[AgentJobKeySecret.DataKey].Should().Be(AgentKeyDerivation.DeriveAgentKey(MasterKey, JobName));
        created.StringData.Values.Should().NotContain(MasterKey, "the master key must never be written into an agent's Secret");
        created.Metadata.OwnerReferences.Should().ContainSingle(o =>
            o.ApiVersion == "batch/v1" && o.Kind == "Job" && o.Name == JobName && o.Uid == "job-uid");
    }

    [Fact]
    public async Task CreateForJobAsync_UnknownJobUid_CreatesSecretWithoutOwner()
    {
        var client = new Mock<IKubernetesJobClient>();
        V1Secret? created = null;
        client.Setup(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), Namespace, It.IsAny<CancellationToken>()))
            .Callback<V1Secret, string, CancellationToken>((s, _, _) => created = s)
            .Returns(Task.CompletedTask);

        await AgentJobKeySecret.CreateForJobAsync(client.Object, Namespace, JobName, jobUid: null, MasterKey, CancellationToken.None);

        created!.Metadata.OwnerReferences.Should().BeNull();
    }

    /// <summary>
    /// A Secret with the same name is left over from an earlier Job with the same name; it is
    /// replaced so garbage collection of that Job cannot delete the new Job's key.
    /// </summary>
    [Fact]
    public async Task CreateForJobAsync_SecretAlreadyExists_ReplacesIt()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.SetupSequence(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), Namespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(Conflict())
            .Returns(Task.CompletedTask);
        client.Setup(c => c.DeleteSecretAsync($"caa-key-{JobName}", Namespace, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await AgentJobKeySecret.CreateForJobAsync(client.Object, Namespace, JobName, "new-uid", MasterKey, CancellationToken.None);

        client.Verify(c => c.DeleteSecretAsync($"caa-key-{JobName}", Namespace, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), Namespace, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateForJobAsync_OtherFailure_Throws()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.CreateSecretAsync(It.IsAny<V1Secret>(), Namespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("forbidden"));

        var act = () => AgentJobKeySecret.CreateForJobAsync(client.Object, Namespace, JobName, "uid", MasterKey, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("the pod cannot start without its key, so the caller must know");
    }

    [Fact]
    public async Task ReadJobUidAsync_ReturnsTheJobsUid()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ReadJobAsync(JobName, Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Uid = "job-uid" } });

        var uid = await AgentJobKeySecret.ReadJobUidAsync(client.Object, Namespace, JobName, CancellationToken.None, NoDelays);

        uid.Should().Be("job-uid");
    }

    [Fact]
    public async Task ReadJobUidAsync_ReadKeepsFailing_ReturnsNullAfterRetries()
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ReadJobAsync(JobName, Namespace, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("api server unavailable"));

        var uid = await AgentJobKeySecret.ReadJobUidAsync(client.Object, Namespace, JobName, CancellationToken.None, NoDelays);

        uid.Should().BeNull();
        client.Verify(c => c.ReadJobAsync(JobName, Namespace, It.IsAny<CancellationToken>()), Times.Exactly(NoDelays.Length + 1));
    }

    [Fact]
    public async Task ReadJobUidAsync_NullJob_ReturnsNull()
    {
        var client = new Mock<IKubernetesJobClient>();

        var uid = await AgentJobKeySecret.ReadJobUidAsync(client.Object, Namespace, JobName, CancellationToken.None, NoDelays);

        uid.Should().BeNull();
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero];

    private static HttpOperationException Conflict() => new("exists")
    {
        Response = new HttpResponseMessageWrapper(new HttpResponseMessage(HttpStatusCode.Conflict), "")
    };
}
