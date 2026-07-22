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
        string providerType = "kiro",
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
            ProviderType = providerType,
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
        string? claimedPvc = null,
        string? opcConfigSecret = null,
        Dictionary<string, string>? projectSecrets = null)
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
            OpencodeConfigSecretName = opcConfigSecret,
            ProjectSecrets = projectSecrets
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
        job.Metadata.Labels["caa/agent-selector"].Should().Be("dotnet.dotnet10.kiro");
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

    [Fact]
    public void Build_FullYamlTemplate_AllPassThroughFieldsDeserializeViaKubernetesJson()
    {
        // Guard against YAML→JSON type mismatches for ALL pass-through fields.
        // Uses the real k8s client serializer (KubernetesJson) to validate that
        // the JsonElements produced by JobTemplateProvider are compatible with
        // the k8s model types. If a numeric field arrives as a JSON string,
        // KubernetesJson.Deserialize will throw — catching the bug at test time.
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "chemsorly/coding-agent:kiro-dotnet10"
          providerType: kiro
          podSecurityContext:
            runAsUser: 1000
            runAsGroup: 1000
            fsGroup: 1000
            runAsNonRoot: true
          initContainers:
            - name: fix-perms
              image: busybox:latest
              command: ["sh", "-c", "chown -R 1000:1000 /data"]
          tolerations:
            - key: agents
              operator: Exists
              effect: NoSchedule
              tolerationSeconds: 300
        """;

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
        var template = provider.Resolve("dotnet,dotnet10,kiro")!;

        // Validate podSecurityContext via k8s client deserializer
        var pscJson = template.PodSecurityContext!.Value.GetRawText();
        var psc = k8s.KubernetesJson.Deserialize<V1PodSecurityContext>(pscJson);
        psc.RunAsUser.Should().Be(1000);
        psc.FsGroup.Should().Be(1000);
        psc.RunAsNonRoot.Should().BeTrue();

        // Validate initContainers via k8s client deserializer
        var icJson = template.InitContainers!.Value.GetRawText();
        var containers = k8s.KubernetesJson.Deserialize<List<V1Container>>(icJson);
        containers.Should().HaveCount(1);
        containers![0].Name.Should().Be("fix-perms");

        // Validate tolerations via k8s client deserializer
        var tolJson = template.Tolerations!.Value.GetRawText();
        var tolerations = k8s.KubernetesJson.Deserialize<List<V1Toleration>>(tolJson);
        tolerations.Should().HaveCount(1);
        tolerations![0].TolerationSeconds.Should().Be(300);
        tolerations[0].Key.Should().Be("agents");
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

    #region AGENT_ID Env Var (Downward API)

    [Fact]
    public void Build_SetsAgentIdFromDownwardApi_PodName()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var agentIdEnv = container.Env.FirstOrDefault(e => e.Name == "AGENT_ID");
        agentIdEnv.Should().NotBeNull("AGENT_ID must be set for SignalR hub authentication");
        agentIdEnv!.ValueFrom.Should().NotBeNull("AGENT_ID should use valueFrom (Downward API)");
        agentIdEnv.ValueFrom.FieldRef.Should().NotBeNull();
        agentIdEnv.ValueFrom.FieldRef.FieldPath.Should().Be("metadata.name",
            "AGENT_ID must reference pod name via Downward API");
    }

    #endregion

    #region AGENT_LABELS Env Var

    [Fact]
    public void Build_PropagatesTemplateLabelsAsEnvVar()
    {
        // K8s-mode WorkItemAgentService reads AGENT_LABELS from the environment
        // to include them in its RegisterAgent message. JobSpecBuilder must inject
        // this env var from the template so the pod has access to its own labels.
        var template = CreateTemplate(labels: "kiro,dotnet,dotnet10");
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var labelsEnv = container.Env.FirstOrDefault(e => e.Name == "AGENT_LABELS");
        labelsEnv.Should().NotBeNull("AGENT_LABELS must be set so the agent can register with its labels");
        labelsEnv!.Value.Should().Be("kiro,dotnet,dotnet10");
    }

    [Fact]
    public void Build_SingleLabel_PropagatedCorrectly()
    {
        // Single label without comma separator should still be propagated
        var template = CreateTemplate(labels: "gpu");
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var labelsEnv = container.Env.FirstOrDefault(e => e.Name == "AGENT_LABELS");
        labelsEnv.Should().NotBeNull("Single-label templates must still propagate AGENT_LABELS");
        labelsEnv!.Value.Should().Be("gpu");
    }

    [Fact]
    public void Build_EmptyLabels_DoesNotIncludeAgentLabelsEnvVar()
    {
        // When template has no labels, don't inject an empty env var
        var template = CreateTemplate(labels: "");
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var labelsEnv = container.Env.FirstOrDefault(e => e.Name == "AGENT_LABELS");
        labelsEnv.Should().BeNull("Empty labels should not produce an AGENT_LABELS env var");
    }

    #endregion

    #region Full Job Spec Validation (K8s API compliance)

    /// <summary>
    /// K8s label value regex: alphanumeric, '-', '_', '.', max 63 chars,
    /// must start and end with alphanumeric (or be empty).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex K8sLabelValueRegex = new(
        @"^(([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9])?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// K8s label key regex (without prefix): alphanumeric, '-', '_', '.', max 63 chars.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex K8sLabelKeyRegex = new(
        @"^([A-Za-z0-9][-A-Za-z0-9_.]*)?[A-Za-z0-9]$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    [Fact]
    public void Build_FullSpec_ProducesValidK8sJob()
    {
        // Comprehensive validation: build a Job with ALL features enabled,
        // then validate it would be accepted by the K8s API — labels, serialization, structure.
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "chemsorly/coding-agent:kiro-dotnet10"
          providerType: kiro
          maxConcurrent: 2
          podSecurityContext:
            runAsUser: 1000
            runAsGroup: 1000
            fsGroup: 1000
            runAsNonRoot: true
          initContainers:
            - name: fix-perms
              image: busybox:latest
              command: ["sh", "-c", "chown -R 1000:1000 /home/ubuntu/.local/share/kiro-cli"]
              volumeMounts:
                - name: kiro-cli-data
                  mountPath: /home/ubuntu/.local/share/kiro-cli
          nodeSelector:
            kubernetes.io/hostname: k8s-worker-1
          tolerations:
            - key: agents
              operator: Exists
              effect: NoSchedule
              tolerationSeconds: 300
          resources:
            requests:
              cpu: "100m"
              memory: "256Mi"
            limits:
              cpu: "4"
              memory: "8Gi"
        """;

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
        var template = provider.Resolve("dotnet,dotnet10,kiro")!;
        var ctx = CreateContext(claimedPvc: "caa-kiro-cli-data-0");

        // Build the full Job (with project secrets to exercise that volume path)
        var ctxWithSecrets = new JobSpecBuilder.BuildContext
        {
            WorkItemId = ctx.WorkItemId,
            AgentSelector = ctx.AgentSelector,
            TimeoutSeconds = ctx.TimeoutSeconds,
            JobName = ctx.JobName,
            ClaimedPvc = ctx.ClaimedPvc,
            OrchestratorUrl = ctx.OrchestratorUrl,
            AgentApiKeySecretName = ctx.AgentApiKeySecretName,
            AgentServiceAccountName = ctx.AgentServiceAccountName,
            Namespace = ctx.Namespace,
            OpencodeConfigSecretName = null,
            ProjectSecrets = new Dictionary<string, string> { ["GH_TOKEN"] = "secret" }
        };

        // Build the full Job
        var job = JobSpecBuilder.Build(template, ctxWithSecrets);

        // ── 1. All label values must be valid K8s labels ──────────────────────
        foreach (var (key, value) in job.Metadata.Labels)
        {
            var keyName = key.Contains('/') ? key.Split('/')[1] : key;
            K8sLabelKeyRegex.IsMatch(keyName).Should().BeTrue(
                $"label key '{key}' has invalid name part '{keyName}'");
            value.Length.Should().BeLessThanOrEqualTo(63,
                $"label value '{value}' for key '{key}' exceeds 63 chars");
            K8sLabelValueRegex.IsMatch(value).Should().BeTrue(
                $"label value '{value}' for key '{key}' is not a valid K8s label value");
        }

        // ── 2. Full Job serializes via KubernetesJson without error ───────────
        var json = k8s.KubernetesJson.Serialize(job);
        json.Should().NotBeNullOrEmpty();

        // ── 3. Round-trip: serialize → deserialize produces equivalent Job ────
        var roundTripped = k8s.KubernetesJson.Deserialize<V1Job>(json);
        roundTripped.Should().NotBeNull();
        roundTripped!.Metadata.Name.Should().Be(job.Metadata.Name);
        roundTripped.Spec.Template.Spec.Containers.Should().HaveCount(1);
        roundTripped.Spec.Template.Spec.Containers[0].Image.Should().Be("chemsorly/coding-agent:kiro-dotnet10");

        // ── 4. Structural invariants ──────────────────────────────────────────
        job.Metadata.Name.Should().NotBeNullOrEmpty("Job must have a name");
        job.Metadata.NamespaceProperty.Should().NotBeNullOrEmpty("Job must have a namespace");
        job.Spec.Template.Spec.RestartPolicy.Should().Be("Never", "Agent Jobs must not restart");
        job.Spec.Template.Spec.ServiceAccountName.Should().NotBeNullOrEmpty("Job must use a ServiceAccount");
        job.Spec.BackoffLimit.Should().BeGreaterThan(0, "Must allow at least one retry");
        job.Spec.TtlSecondsAfterFinished.Should().BeGreaterThan(0, "Jobs must auto-cleanup");

        // ── 5. Security: container drops ALL capabilities ─────────────────────
        var mainContainer = job.Spec.Template.Spec.Containers[0];
        mainContainer.SecurityContext.Capabilities.Drop.Should().Contain("ALL");

        // ── 6. Volumes: all volumeMounts have corresponding volumes ───────────
        var volumeNames = job.Spec.Template.Spec.Volumes.Select(v => v.Name).ToHashSet();
        foreach (var mount in mainContainer.VolumeMounts)
        {
            volumeNames.Should().Contain(mount.Name,
                $"container volumeMount '{mount.Name}' has no corresponding volume");
        }
        if (job.Spec.Template.Spec.InitContainers is not null)
        {
            foreach (var ic in job.Spec.Template.Spec.InitContainers)
            {
                if (ic.VolumeMounts is null) continue;
                foreach (var mount in ic.VolumeMounts)
                {
                    volumeNames.Should().Contain(mount.Name,
                        $"initContainer '{ic.Name}' volumeMount '{mount.Name}' has no corresponding volume");
                }
            }
        }
    }

    #endregion

    #region OTEL Env Var Propagation

    [Fact]
    public void Build_WhenOtelEndpointSet_PropagatesOtelEndpoint()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318");
        try
        {
            var template = CreateTemplate();
            var ctx = CreateContext();

            var job = JobSpecBuilder.Build(template, ctx);

            var container = job.Spec.Template.Spec.Containers[0];
            var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_ENDPOINT");
            env.Should().NotBeNull();
            env!.Value.Should().Be("http://collector:4318");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }

    [Fact]
    public void Build_WhenOtelProtocolSet_PropagatesOtelProtocol()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        try
        {
            var template = CreateTemplate();
            var ctx = CreateContext();

            var job = JobSpecBuilder.Build(template, ctx);

            var container = job.Spec.Template.Spec.Containers[0];
            var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_PROTOCOL");
            env.Should().NotBeNull("OTEL_EXPORTER_OTLP_PROTOCOL must be propagated for agent OTLP export to work");
            env!.Value.Should().Be("http/protobuf");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        }
    }

    [Fact]
    public void Build_WhenOtelResourceAttributesSet_PropagatesResourceAttributes()
    {
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=production,service.namespace=coding-agent");
        try
        {
            var template = CreateTemplate();
            var ctx = CreateContext();

            var job = JobSpecBuilder.Build(template, ctx);

            var container = job.Spec.Template.Spec.Containers[0];
            var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
            env.Should().NotBeNull("OTEL_RESOURCE_ATTRIBUTES must be propagated for agent trace correlation");
            env!.Value.Should().Be("deployment.environment=production,service.namespace=coding-agent");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
        }
    }

    [Fact]
    public void Build_WhenOtelVarsNotSet_DoesNotIncludeValueBasedOnes()
    {
        // Ensure env vars are clear
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);

        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_ENDPOINT").Should().BeNull();
        container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_PROTOCOL").Should().BeNull();
        container.Env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES").Should().BeNull();
    }

    [Fact]
    public void Build_OtelHeaders_UsesSecretKeyRefNotPlaintext()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_HEADERS");
        env.Should().NotBeNull("OTEL_EXPORTER_OTLP_HEADERS must always be injected via Secret");
        env!.Value.Should().BeNull("Headers must not be in plaintext Value");
        env.ValueFrom.Should().NotBeNull();
        env.ValueFrom!.SecretKeyRef.Should().NotBeNull();
        env.ValueFrom.SecretKeyRef!.Name.Should().Be("caa-secret");
        env.ValueFrom.SecretKeyRef.Key.Should().Be("otel-headers");
        env.ValueFrom.SecretKeyRef.Optional.Should().BeTrue("Secret key may not exist in all deployments");
    }

    [Fact]
    public void Build_SetsPerJobOtelServiceName()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();
        ctx.JobName = "caa-abcdef12";

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_SERVICE_NAME");
        env.Should().NotBeNull("OTEL_SERVICE_NAME must be set for per-job trace attribution");
        env!.Value.Should().Be("coding-agent-worker-caa-abcdef12");
    }

    #endregion

    #region LOG_LEVEL Propagation

    [Fact]
    public void Build_WhenLogLevelSet_PropagatesLogLevel()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", "Debug");
        try
        {
            var template = CreateTemplate();
            var ctx = CreateContext();

            var job = JobSpecBuilder.Build(template, ctx);

            var container = job.Spec.Template.Spec.Containers[0];
            var env = container.Env.FirstOrDefault(e => e.Name == "LOG_LEVEL");
            env.Should().NotBeNull();
            env!.Value.Should().Be("Debug");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);
        }
    }

    [Fact]
    public void Build_WhenLogLevelNotSet_DoesNotIncludeLogLevel()
    {
        Environment.SetEnvironmentVariable("LOG_LEVEL", null);

        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Env.FirstOrDefault(e => e.Name == "LOG_LEVEL").Should().BeNull();
    }

    #endregion

    #region OpenCode Provider

    [Fact]
    public void Build_OpencodeAgent_WithConfigSecret_MountsOpencodeConfigVolume()
    {
        var template = CreateTemplate(providerType: "opencode", image: "chemsorly/coding-agent:opencode");
        var ctx = CreateContext(opcConfigSecret: "opencode-config-secret");

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "opencode-config");
        var opcVol = volumes.First(v => v.Name == "opencode-config");
        opcVol.Secret.Should().NotBeNull();
        opcVol.Secret.SecretName.Should().Be("opencode-config-secret");

        var container = job.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().Contain(vm => vm.Name == "opencode-config");
        var opcMount = container.VolumeMounts.First(vm => vm.Name == "opencode-config");
        opcMount.MountPath.Should().Be("/home/ubuntu/.config/opencode");
        opcMount.ReadOnlyProperty.Should().BeTrue();
    }

    [Fact]
    public void Build_OpencodeAgent_WithoutConfigSecret_NoOpencodeVolume()
    {
        var template = CreateTemplate(providerType: "opencode", image: "chemsorly/coding-agent:opencode");
        var ctx = CreateContext(opcConfigSecret: null);

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "opencode-config");

        var container = job.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().NotContain(vm => vm.Name == "opencode-config");
    }

    [Fact]
    public void Build_NonKiroAgent_WithPvc_DoesNotMountKiroVolume()
    {
        // The condition is `isKiroAgent && ctx.ClaimedPvc is not null` — a non-kiro provider
        // with a ClaimedPvc should NOT get the kiro-cli-data volume mount.
        var template = CreateTemplate(providerType: "opencode", image: "chemsorly/coding-agent:opencode");
        var ctx = CreateContext(claimedPvc: "some-pvc-claim");

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "kiro-cli-data");

        var container = job.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().NotContain(vm => vm.Name == "kiro-cli-data");
    }

    #endregion

    #region ProjectSecrets Boundary

    [Fact]
    public void Build_EmptyProjectSecrets_NoProjectSecretsVolume()
    {
        // Empty (non-null) dictionary should NOT generate project-secrets volume
        // — the production code guards with `Count > 0`.
        var template = CreateTemplate();
        var ctx = CreateContext(projectSecrets: new Dictionary<string, string>());

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "project-secrets");

        var container = job.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().NotContain(vm => vm.Name == "project-secrets");
    }

    [Fact]
    public void Build_WithProjectSecrets_CreatesCorrectlyNamedSecret()
    {
        // Secret name must be `caa-secrets-{first 8 hex chars of WorkItemId}`
        var workItemId = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");
        var template = CreateTemplate();
        var ctx = CreateContext(
            workItemId: workItemId,
            projectSecrets: new Dictionary<string, string> { ["MY_SECRET"] = "value" });

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "project-secrets");
        var secretVol = volumes.First(v => v.Name == "project-secrets");
        secretVol.Secret.Should().NotBeNull();
        // WorkItemId "abcdef12-3456-7890-abcd-ef1234567890" → ToString("N") = "abcdef1234567890abcdef1234567890" → [..8] = "abcdef12"
        secretVol.Secret.SecretName.Should().Be("caa-secrets-abcdef12");
        secretVol.Secret.Optional.Should().BeTrue();

        var container = job.Spec.Template.Spec.Containers[0];
        container.VolumeMounts.Should().Contain(vm => vm.Name == "project-secrets");
        var secretMount = container.VolumeMounts.First(vm => vm.Name == "project-secrets");
        secretMount.MountPath.Should().Be("/var/run/secrets/project-secrets");
        secretMount.ReadOnlyProperty.Should().BeTrue();
    }

    #endregion
}
