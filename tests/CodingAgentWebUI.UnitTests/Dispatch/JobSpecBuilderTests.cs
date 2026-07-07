using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using k8s.Models;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="JobSpecBuilder"/> — applies <see cref="JobTemplate"/> fields
/// to a K8s Job pod spec (initContainers, podSecurityContext, nodeSelector, resources, tolerations).
/// </summary>
public class JobSpecBuilderTests
{
    private static JobTemplate CreateTemplate(
        string labels = "dotnet,dotnet10,kiro",
        string image = "chemsorly/coding-agent:kiro-dotnet10",
        string? resourcesJson = null,
        string? podSecurityContextJson = null,
        string? nodeSelectorJson = null,
        string? initContainersJson = null,
        string? tolerationsJson = null)
    {
        return new JobTemplate
        {
            Labels = labels,
            Image = image,
            ProviderType = "kiro",
            MaxConcurrent = 2,
            Resources = resourcesJson is not null
                ? JsonSerializer.Deserialize<JobTemplateResources>(resourcesJson, JobTemplateProvider.JsonOptions)
                : null,
            PodSecurityContext = podSecurityContextJson is not null
                ? JsonDocument.Parse(podSecurityContextJson).RootElement
                : null,
            NodeSelector = nodeSelectorJson is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(nodeSelectorJson, JobTemplateProvider.JsonOptions)
                : null,
            InitContainers = initContainersJson is not null
                ? JsonDocument.Parse(initContainersJson).RootElement
                : null,
            Tolerations = tolerationsJson is not null
                ? JsonDocument.Parse(tolerationsJson).RootElement
                : null
        };
    }

    private static JobSpecBuilder.BuildContext CreateContext(
        Guid? workItemId = null,
        string agentSelector = "dotnet,dotnet10,kiro",
        int timeoutSeconds = 1800,
        string? claimedPvc = null)
    {
        return new JobSpecBuilder.BuildContext
        {
            WorkItemId = workItemId ?? Guid.NewGuid(),
            AgentSelector = agentSelector,
            TimeoutSeconds = timeoutSeconds,
            JobName = "caa-12345678",
            ClaimedPvc = claimedPvc,
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            Namespace = "coding-agent",
            OpencodeConfigSecretName = null,
            ProjectSecrets = null
        };
    }

    #region Resources

    [Fact]
    public void Build_WithResources_AppliesRequestsAndLimits()
    {
        var template = CreateTemplate(resourcesJson: """
            { "requests": { "cpu": "100m", "memory": "256Mi" }, "limits": { "cpu": "2", "memory": "4Gi" } }
        """);
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Resources.Should().NotBeNull();
        container.Resources.Requests["cpu"].ToString().Should().Be("100m");
        container.Resources.Requests["memory"].ToString().Should().Be("256Mi");
        container.Resources.Limits["cpu"].ToString().Should().Be("2");
        container.Resources.Limits["memory"].ToString().Should().Be("4Gi");
    }

    [Fact]
    public void Build_WithoutResources_NoResourcesSet()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Resources.Should().BeNull();
    }

    #endregion

    #region PodSecurityContext

    [Fact]
    public void Build_WithPodSecurityContext_AppliesRunAsUserAndFsGroup()
    {
        var template = CreateTemplate(podSecurityContextJson: """
            { "runAsUser": 1000, "runAsGroup": 1000, "fsGroup": 1000 }
        """);
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var podSec = job.Spec.Template.Spec.SecurityContext;
        podSec.Should().NotBeNull();
        podSec.RunAsUser.Should().Be(1000);
        podSec.RunAsGroup.Should().Be(1000);
        podSec.FsGroup.Should().Be(1000);
    }

    [Fact]
    public void Build_WithoutPodSecurityContext_UsesHardenedDefaults()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var podSec = job.Spec.Template.Spec.SecurityContext;
        podSec.Should().NotBeNull();
        podSec.RunAsNonRoot.Should().BeTrue();
        podSec.SeccompProfile.Should().NotBeNull();
    }

    #endregion

    #region NodeSelector

    [Fact]
    public void Build_WithNodeSelector_AppliedToPodSpec()
    {
        var template = CreateTemplate(nodeSelectorJson: """
            { "kubernetes.io/hostname": "k8s-deb-1" }
        """);
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.NodeSelector.Should().NotBeNull();
        job.Spec.Template.Spec.NodeSelector["kubernetes.io/hostname"].Should().Be("k8s-deb-1");
    }

    [Fact]
    public void Build_WithoutNodeSelector_NullOnPodSpec()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.NodeSelector.Should().BeNull();
    }

    #endregion

    #region InitContainers

    [Fact]
    public void Build_WithInitContainers_AppliedToPodSpec()
    {
        var template = CreateTemplate(initContainersJson: """
            [{ "name": "fix-perms", "image": "busybox:latest", "command": ["sh", "-c", "chown -R 1000:1000 /data"] }]
        """);
        var ctx = CreateContext(claimedPvc: "kiro-creds-pvc-1");

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.InitContainers.Should().NotBeNull();
        job.Spec.Template.Spec.InitContainers.Should().HaveCount(1);
        job.Spec.Template.Spec.InitContainers[0].Name.Should().Be("fix-perms");
        job.Spec.Template.Spec.InitContainers[0].Image.Should().Be("busybox:latest");
        job.Spec.Template.Spec.InitContainers[0].Command.Should().Contain("sh");
    }

    [Fact]
    public void Build_WithoutInitContainers_NullOnPodSpec()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.InitContainers.Should().BeNull();
    }

    #endregion

    #region Tolerations

    [Fact]
    public void Build_WithTolerations_AppliedToPodSpec()
    {
        var template = CreateTemplate(tolerationsJson: """
            [{ "key": "agents", "operator": "Exists", "effect": "NoSchedule" }]
        """);
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.Tolerations.Should().NotBeNull();
        job.Spec.Template.Spec.Tolerations.Should().HaveCount(1);
        job.Spec.Template.Spec.Tolerations[0].Key.Should().Be("agents");
        job.Spec.Template.Spec.Tolerations[0].OperatorProperty.Should().Be("Exists");
        job.Spec.Template.Spec.Tolerations[0].Effect.Should().Be("NoSchedule");
    }

    [Fact]
    public void Build_WithoutTolerations_NullOnPodSpec()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.Tolerations.Should().BeNull();
    }

    #endregion

    #region Core Fields

    [Fact]
    public void Build_SetsImageFromTemplate()
    {
        var template = CreateTemplate(image: "custom-image:v2");
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.Containers[0].Image.Should().Be("custom-image:v2");
    }

    [Fact]
    public void Build_SetsJobMetadata()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var template = CreateTemplate();
        var ctx = CreateContext(workItemId: id);
        ctx.JobName = "caa-12345678";

        var job = JobSpecBuilder.Build(template, ctx);

        job.Metadata.Name.Should().Be("caa-12345678");
        job.Metadata.NamespaceProperty.Should().Be("coding-agent");
        job.Metadata.Labels["caa/work-item-id"].Should().Be(id.ToString());
        job.Metadata.Labels["caa/agent-selector"].Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void Build_KiroAgent_MountsPvcWhenClaimed()
    {
        var template = CreateTemplate();
        var ctx = CreateContext(claimedPvc: "kiro-creds-pvc-1");

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "kiro-cli-data");
        var pvcVol = volumes.First(v => v.Name == "kiro-cli-data");
        pvcVol.PersistentVolumeClaim.ClaimName.Should().Be("kiro-creds-pvc-1");
    }

    [Fact]
    public void Build_KiroAgent_NoPvc_NoVolume()
    {
        var template = CreateTemplate();
        var ctx = CreateContext(claimedPvc: null);

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "kiro-cli-data");
    }

    [Fact]
    public void Build_SetsActiveDeadlineFromTimeout()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();
        ctx.TimeoutSeconds = 1800;

        var job = JobSpecBuilder.Build(template, ctx);

        // timeout + 60s buffer
        job.Spec.ActiveDeadlineSeconds.Should().Be(1860);
    }

    #endregion

    #region PodSecurityContext — YAML round-trip

    [Fact]
    public void Build_WithPodSecurityContextFromYaml_NumericFieldsDeserializeCorrectly()
    {
        // Reproduces production bug: YAML integers in podSecurityContext get serialized
        // as JSON strings through the YamlDotNet -> Dictionary<string, object> -> JsonElement path,
        // causing "Cannot get the value of a token type 'String' as a number" at runtime.
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "chemsorly/coding-agent:kiro-dotnet10"
          providerType: kiro
          podSecurityContext:
            runAsUser: 1000
            runAsGroup: 1000
            fsGroup: 1000
        """;

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
        var template = provider.Resolve("dotnet,dotnet10,kiro")!;
        var ctx = CreateContext();

        // This call throws if fsGroup is a JSON string instead of number
        var job = JobSpecBuilder.Build(template, ctx);

        var podSec = job.Spec.Template.Spec.SecurityContext;
        podSec.RunAsUser.Should().Be(1000);
        podSec.RunAsGroup.Should().Be(1000);
        podSec.FsGroup.Should().Be(1000);
    }

    #endregion

    #region InitContainers VolumeMounts Injection

    [Fact]
    public void Build_InitContainers_WithKiroPvc_GetVolumeAutoMounted()
    {
        // initContainers reference "kiro-cli-data" volume — verify it's available
        var template = CreateTemplate(initContainersJson: """
            [{
              "name": "fix-perms",
              "image": "busybox:latest",
              "command": ["sh", "-c", "chown -R 1000:1000 /home/ubuntu/.local/share/kiro-cli"],
              "volumeMounts": [{ "name": "kiro-cli-data", "mountPath": "/home/ubuntu/.local/share/kiro-cli" }]
            }]
        """);
        var ctx = CreateContext(claimedPvc: "kiro-creds-pvc-1");

        var job = JobSpecBuilder.Build(template, ctx);

        // The initContainer should have its volumeMount preserved
        var initContainer = job.Spec.Template.Spec.InitContainers[0];
        initContainer.VolumeMounts.Should().Contain(vm => vm.Name == "kiro-cli-data");
    }

    #endregion
}
