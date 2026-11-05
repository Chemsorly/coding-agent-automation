using AwesomeAssertions;
using CodingAgent.Kubernetes;
using k8s.Models;
using Moq;
using Xunit;

namespace CodingAgent.JobController.UnitTests.Dispatch;

/// <summary>Spec 057 req 2.1, 2.2, DD1, DD2: counting running agent Jobs per job template.</summary>
public sealed class AgentJobSnapshotTests
{
    private const string Kiro = "dotnet,dotnet10,kiro";
    private const string Claude = "claude,dotnet,dotnet10";

    private static V1Job Job(string? selectorLabel, bool run, string? pvc = null, string? terminal = null)
    {
        var labels = new Dictionary<string, string> { ["app.kubernetes.io/component"] = "agent-job" };
        if (selectorLabel is not null) labels["caa/agent-selector"] = selectorLabel;
        if (run) labels["caa/work-item-id"] = Guid.NewGuid().ToString();
        return new V1Job
        {
            Metadata = new V1ObjectMeta { Name = $"caa-{Guid.NewGuid():N}"[..12], Labels = labels },
            Spec = new V1JobSpec
            {
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        Containers = [],
                        Volumes = pvc is null ? [] : [new V1Volume
                        {
                            Name = "creds",
                            PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvc }
                        }]
                    }
                }
            },
            Status = terminal is null ? new V1JobStatus() : new V1JobStatus
            {
                Conditions = [new V1JobCondition { Type = terminal, Status = "True" }]
            }
        };
    }

    private static async Task<AgentJobSnapshot> ReadAsync(IEnumerable<string> keys, params V1Job[] jobs)
    {
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ListJobsAsync("ns", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = jobs });
        return await AgentJobSnapshot.ReadAsync(client.Object, "ns", keys.ToList(), CancellationToken.None);
    }

    // req 2.1, 2.2
    [Fact]
    public async Task WhenJobsRun_ShouldCountRunsAndChatsPerTemplate_AndSkipEndedJobs()
    {
        var snapshot = await ReadAsync([Kiro, Claude],
            Job("dotnet.dotnet10.kiro", run: true),
            Job("dotnet.dotnet10.kiro", run: true),
            Job("dotnet.dotnet10.kiro", run: false),
            Job("claude.dotnet.dotnet10", run: true, terminal: "Complete"),
            Job("claude.dotnet.dotnet10", run: false, terminal: "Failed"));

        snapshot.Counts.ByTemplate[Kiro].Should().Be((2, 1));
        snapshot.Counts.ByTemplate[Claude].Should().Be((0, 0));
        snapshot.Counts.UnknownTemplate.Should().Be((0, 0));
        snapshot.Counts.Total.Should().Be(3);
    }

    // req 2.1: a missing label or one that matches no deployed template counts toward the total only.
    [Fact]
    public async Task WhenALabelIsMissingOrUnknown_ShouldCountItAsUnknownTemplate()
    {
        var snapshot = await ReadAsync([Kiro],
            Job(null, run: true),
            Job("old.template", run: false));

        snapshot.Counts.ByTemplate[Kiro].Should().Be((0, 0));
        snapshot.Counts.UnknownTemplate.Should().Be((1, 1));
        snapshot.Counts.Total.Should().Be(2);
    }

    // DD2: the transform is applied to the keys, so a key that contains a dot still maps.
    [Fact]
    public async Task WhenAKeyContainsADot_ShouldStillMapItsJobs()
    {
        var snapshot = await ReadAsync(["python3.12,kiro"], Job("python3.12.kiro", run: true));

        snapshot.Counts.ByTemplate["python3.12,kiro"].Should().Be((1, 0));
    }

    // req 1.4: the template without labels has the key "" and the label value "".
    [Fact]
    public async Task WhenTheTemplateHasNoLabels_ShouldMapTheEmptyLabel()
    {
        var snapshot = await ReadAsync([""], Job("", run: true));

        snapshot.Counts.ByTemplate[""].Should().Be((1, 0));
        snapshot.Counts.UnknownTemplate.Should().Be((0, 0));
    }

    // DD2: colliding keys share the Job's count; the total counts it once.
    [Fact]
    public async Task WhenTwoKeysGiveTheSameLabel_ShouldCountTheJobForBoth_AndTheTotalOnce()
    {
        var snapshot = await ReadAsync(["a.b", "a,b"], Job("a.b", run: true));

        snapshot.Counts.ByTemplate["a.b"].Should().Be((1, 0));
        snapshot.Counts.ByTemplate["a,b"].Should().Be((1, 0));
        snapshot.Counts.Total.Should().Be(1);
    }

    // DD1: the snapshot's held PVCs equal what AgentJobPvcs reports for the same Jobs.
    [Fact]
    public async Task HeldPvcs_ShouldEqualAgentJobPvcsForTheSameJobs()
    {
        var jobs = new[]
        {
            Job("dotnet.dotnet10.kiro", run: true, pvc: "pvc-0"),
            Job("dotnet.dotnet10.kiro", run: false, pvc: "pvc-1"),
            Job("dotnet.dotnet10.kiro", run: true, pvc: "pvc-2", terminal: "Complete"),
        };
        var client = new Mock<IKubernetesJobClient>();
        client.Setup(c => c.ListJobsAsync("ns", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = jobs });

        var snapshot = await AgentJobSnapshot.ReadAsync(client.Object, "ns", [Kiro], CancellationToken.None);
        var held = await AgentJobPvcs.ListHeldAsync(client.Object, "ns", CancellationToken.None);

        snapshot.HeldPvcs.Should().BeEquivalentTo(held);
        snapshot.HeldPvcs.Should().BeEquivalentTo(["pvc-0", "pvc-1"]);
    }

    [Fact]
    public async Task WhenTheListIsNull_ShouldCountNoJobs()
    {
        var client = new Mock<IKubernetesJobClient>();

        var snapshot = await AgentJobSnapshot.ReadAsync(client.Object, "ns", [Kiro], CancellationToken.None);

        snapshot.Counts.Total.Should().Be(0);
        snapshot.Counts.ByTemplate[Kiro].Should().Be((0, 0));
        snapshot.HeldPvcs.Should().BeEmpty();
    }

    [Fact]
    public void LabelCollisions_ShouldReturnOnlyGroupsOfTwoOrMore()
    {
        var collisions = AgentJobSnapshot.LabelCollisions(["a.b", "a,b", Kiro, Claude]);

        collisions.Should().ContainSingle().Which.Should().Equal("a,b", "a.b");
    }
}
