using System.Text.Json;
using AwesomeAssertions;
using k8s.Models;
using Xunit;

namespace CodingAgent.JobController.UnitTests.Dispatch;

/// <summary>Unit tests for JobSpecBuilder — the Kubernetes-assembly variant.</summary>
/// <remarks>
/// This class mutates LOG_LEVEL, OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL,
/// and OTEL_RESOURCE_ATTRIBUTES environment variables. [Collection] prevents parallel
/// execution with other test classes that depend on the same process-global env state.
/// </remarks>
[Collection("EnvironmentVariables")]
public sealed class JobSpecBuilderTests
{
    // ── Base context helpers ─────────────────────────────────────────────────

    private static JobTemplate KiroTemplate(string? podSecurityContextJson = null) => new()
    {
        Labels = "dotnet,kiro",
        Image = "agent:latest",
        ProviderType = "kiro",
        MaxConcurrent = 2
    };

    private static JobTemplate OpenCodeTemplate() => new()
    {
        Labels = "dotnet,opencode",
        Image = "opencode-agent:latest",
        ProviderType = "opencode",
        MaxConcurrent = 0
    };

    private static JobTemplate GenericTemplate() => new()
    {
        Labels = "java",
        Image = "java-agent:latest",
        ProviderType = "generic",
        MaxConcurrent = 0
    };

    private static JobSpecBuilder.BuildContext BaseCtx(Guid? workItemId = null) => new()
    {
        WorkItemId = workItemId,
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        JobName = "caa-test-job",
        ClaimedPvc = null,
        OrchestratorUrl = "http://orchestrator:5000",
        AgentApiKeySecretName = "agent-api-key",
        AgentServiceAccountName = "agent-sa",
        Namespace = "default"
    };

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
                ? JsonSerializer.Deserialize<JobTemplateResources>(resourcesJson, JobTemplateStore.JsonOptions)
                : null,
            PodSecurityContext = podSecurityContextJson is not null
                ? JsonDocument.Parse(podSecurityContextJson).RootElement
                : null,
            NodeSelector = nodeSelectorJson is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(nodeSelectorJson, JobTemplateStore.JsonOptions)
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

    // ── Pod failures ─────────────────────────────────────────────────────────

    /// <summary>
    /// Drains, evictions, preemption and node loss (pod condition DisruptionTarget) are replaced
    /// without spending backoffLimit, which stays for crashes and OOM kills. A replacement waits for
    /// the old pod to end, so two pods never run the same work item.
    /// </summary>
    [Fact]
    public void Build_DisruptionsDoNotCountAgainstBackoffLimit_AndReplacementWaitsForOldPod()
    {
        var job = JobSpecBuilder.Build(KiroTemplate(), BaseCtx(Guid.NewGuid()));

        job.Spec.BackoffLimit.Should().Be(2);
        job.Spec.PodReplacementPolicy.Should().Be("Failed");
        var rule = job.Spec.PodFailurePolicy!.Rules.Should().ContainSingle().Subject;
        rule.Action.Should().Be("Ignore");
        rule.OnExitCodes.Should().BeNull();
        rule.OnPodConditions.Should().ContainSingle(c => c.Type == "DisruptionTarget" && c.Status == "True");
        job.Spec.Template.Spec.RestartPolicy.Should().Be("Never", "podFailurePolicy requires restartPolicy Never");
    }

    // ── Agent key (Spec 043 Req 8a) ──────────────────────────────────────────

    /// <summary>
    /// Every agent Job — work item or consolidation (WorkItemId set), chat or model fetch (null) —
    /// reads its key from its own per-Job Secret as the AGENT_API_KEY env var, which the agent uses
    /// as-is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_AgentApiKeyComesFromThePerJobSecret(bool isWorkItem)
    {
        var ctx = BaseCtx(workItemId: isWorkItem ? Guid.NewGuid() : null);

        var job = JobSpecBuilder.Build(KiroTemplate(), ctx);

        var apiKeyEnv = job.Spec.Template.Spec.Containers[0].Env.Single(e => e.Name == "AGENT_API_KEY");
        apiKeyEnv.ValueFrom!.SecretKeyRef!.Name.Should().Be("caa-key-caa-test-job");
        apiKeyEnv.ValueFrom.SecretKeyRef.Key.Should().Be("agent-api-key");
    }

    /// <summary>
    /// The chart Secret also holds the master key; a pod may read only its otel-headers entry.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_NeverExposesTheMasterKeyToThePod(bool isWorkItem)
    {
        var ctx = BaseCtx(workItemId: isWorkItem ? Guid.NewGuid() : null);

        var job = JobSpecBuilder.Build(GenericTemplate(), ctx);

        var podSpec = job.Spec.Template.Spec;
        var env = podSpec.Containers[0].Env;
        env.Should().NotContain(e => e.Name == "AGENT_API_KEY_FILE",
            "dispatched pods must not use the master-key file path");
        env.Should().NotContain(
            e => e.ValueFrom != null && e.ValueFrom.SecretKeyRef != null
                && e.ValueFrom.SecretKeyRef.Name == ctx.AgentApiKeySecretName
                && e.ValueFrom.SecretKeyRef.Key == "agent-api-key",
            "no env var may read the master key from the chart Secret");
        (podSpec.Volumes ?? []).Should().NotContain(
            v => v.Secret != null && v.Secret.SecretName == ctx.AgentApiKeySecretName,
            "the chart Secret holding the master key must not be mounted");
    }

    // ── OpenCode without config secret ───────────────────────────────────────

    [Fact]
    public void OpenCode_WithoutConfigSecretName_NoOpencodeEnvVar()
    {
        var template = OpenCodeTemplate();
        var ctx = BaseCtx() with
        {
            OpencodeConfigSecretName = null  // no config secret
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().NotContain(e => e.Name == "OPENCODE_CONFIG_CONTENT",
            "OPENCODE_CONFIG_CONTENT must not be added when OpencodeConfigSecretName is null");
    }

    [Fact]
    public void OpenCode_WithEmptyConfigSecretName_NoOpencodeEnvVar()
    {
        var template = OpenCodeTemplate();
        var ctx = BaseCtx() with
        {
            OpencodeConfigSecretName = ""
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().NotContain(e => e.Name == "OPENCODE_CONFIG_CONTENT",
            "OPENCODE_CONFIG_CONTENT must not be added when OpencodeConfigSecretName is empty");
    }

    // ── WorkItemId = null ────────────────────────────────────────────────────

    [Fact]
    public void WhenWorkItemId_Null_NoWorkItemIdLabel_NoCliArg()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: null);

        var job = JobSpecBuilder.Build(template, ctx);

        // No caa/work-item-id label
        job.Metadata.Labels.Should().NotContainKey("caa/work-item-id");

        // No --work-item-id arg
        var args = job.Spec.Template.Spec.Containers[0].Args;
        args.Should().NotContain(a => a.StartsWith("--work-item-id"));
    }

    [Fact]
    public void WhenWorkItemId_Set_WorkItemIdLabel_And_CliArg_Present()
    {
        var id = Guid.NewGuid();
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: id);

        var job = JobSpecBuilder.Build(template, ctx);

        job.Metadata.Labels.Should().ContainKey("caa/work-item-id");
        job.Metadata.Labels["caa/work-item-id"].Should().Be(id.ToString());

        var args = job.Spec.Template.Spec.Containers[0].Args;
        args.Should().Contain(a => a.StartsWith("--work-item-id="));
    }

    // ── Kiro agent without PVC ────────────────────────────────────────────────

    [Fact]
    public void KiroAgent_NullPvc_NoKiroCliDataVolume()
    {
        var template = KiroTemplate();
        var ctx = BaseCtx() with { ClaimedPvc = null };

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes?.Should().NotContain(v => v.Name == "kiro-cli-data");
    }

    [Fact]
    public void KiroAgent_WithPvc_KiroCliDataVolumePresent()
    {
        var template = KiroTemplate();
        var ctx = BaseCtx() with { ClaimedPvc = "pvc-kiro-1" };

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "kiro-cli-data");
        var volume = volumes!.Single(v => v.Name == "kiro-cli-data");
        volume.PersistentVolumeClaim!.ClaimName.Should().Be("pvc-kiro-1");
    }

    // ── ProjectSecrets volume ─────────────────────────────────────────────────

    [Fact]
    public void WorkItem_NullProjectSecrets_NoProjectSecretsVolume()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: Guid.NewGuid()) with
        {
            ProjectSecrets = null
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes?.Should().NotContain(v => v.Name == "project-secrets");
    }

    // ── Default PodSecurityContext ────────────────────────────────────────────

    [Fact]
    public void WhenNoPodSecurityContextInTemplate_DefaultHardenedContextApplied()
    {
        var template = GenericTemplate();  // no PodSecurityContext field
        var ctx = BaseCtx();

        var job = JobSpecBuilder.Build(template, ctx);

        var psc = job.Spec.Template.Spec.SecurityContext;
        psc.Should().NotBeNull();
        psc!.RunAsNonRoot.Should().BeTrue();
        psc.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    // ── Container capability drops ────────────────────────────────────────────

    [Fact]
    public void Container_AlwaysDropsAllCapabilities()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx();

        var job = JobSpecBuilder.Build(template, ctx);

        var caps = job.Spec.Template.Spec.Containers[0].SecurityContext!.Capabilities;
        caps!.Drop.Should().Contain("ALL");
    }

    // ── OpenCode WITH config secret ───────────────────────────────────────────

    [Fact]
    public void OpenCode_WithConfigSecretName_InjectsOpencodeConfigContentEnvVar()
    {
        var template = OpenCodeTemplate();
        var ctx = BaseCtx(Guid.NewGuid()) with
        {
            OpencodeConfigSecretName = "opencode-config-secret"
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        var configEnv = env.SingleOrDefault(e => e.Name == "OPENCODE_CONFIG_CONTENT");
        configEnv.Should().NotBeNull("OPENCODE_CONFIG_CONTENT must be injected for opencode agents with config secret");
        configEnv!.ValueFrom.Should().NotBeNull();
        configEnv.ValueFrom!.SecretKeyRef!.Name.Should().Be("opencode-config-secret");
        configEnv.ValueFrom.SecretKeyRef.Key.Should().Be("opencode-config-content");
        configEnv.ValueFrom.SecretKeyRef.Optional.Should().BeTrue();
    }

    [Fact]
    public void NonOpenCode_WithConfigSecretName_DoesNotInjectOpencodeEnvVar()
    {
        // Non-opencode agent (kiro) with OpencodeConfigSecretName — must not inject env var
        var template = KiroTemplate();
        var ctx = BaseCtx(Guid.NewGuid()) with
        {
            OpencodeConfigSecretName = "opencode-config-secret"
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().NotContain(e => e.Name == "OPENCODE_CONFIG_CONTENT",
            "only opencode agents get OPENCODE_CONFIG_CONTENT");
    }

    // ── Claude credentials ───────────────────────────────────────────────────

    private static JobTemplate ClaudeTemplate() => new()
    {
        Labels = "claude,dotnet",
        Image = "claude-agent:latest",
        ProviderType = "claude",
        MaxConcurrent = 2
    };

    [Fact]
    public void Claude_WithAuthSecretName_InjectsBothOptionalCredentials_UnderPipelineNames()
    {
        var ctx = BaseCtx(Guid.NewGuid()) with { ClaudeAuthSecretName = "agent-secret" };

        var job = JobSpecBuilder.Build(ClaudeTemplate(), ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        var apiKey = env.Single(e => e.Name == "AGENT_CLAUDE_API_KEY").ValueFrom!.SecretKeyRef!;
        apiKey.Name.Should().Be("agent-secret");
        apiKey.Key.Should().Be(JobSpecBuilder.ClaudeApiKeySecretKey);
        apiKey.Optional.Should().BeTrue();
        var oauthToken = env.Single(e => e.Name == "AGENT_CLAUDE_OAUTH_TOKEN").ValueFrom!.SecretKeyRef!;
        oauthToken.Key.Should().Be(JobSpecBuilder.ClaudeOAuthTokenSecretKey);
        oauthToken.Optional.Should().BeTrue();
        env.Should().NotContain(e => e.Name == "ANTHROPIC_API_KEY" || e.Name == "CLAUDE_CODE_OAUTH_TOKEN",
            "only the claude process may see the CLI's own credential variables");
        job.Spec.Template.Spec.Volumes.Should().BeNullOrEmpty("claude agents need no credential PVC");
    }

    [Fact]
    public void Claude_WithoutAuthSecretName_InjectsNoCredentials()
    {
        var job = JobSpecBuilder.Build(ClaudeTemplate(), BaseCtx(Guid.NewGuid()));

        job.Spec.Template.Spec.Containers[0].Env.Should().NotContain(e => e.Name.StartsWith("AGENT_CLAUDE_"));
    }

    [Fact]
    public void NonClaude_WithAuthSecretName_InjectsNoCredentials()
    {
        var job = JobSpecBuilder.Build(KiroTemplate(), BaseCtx(Guid.NewGuid()) with { ClaudeAuthSecretName = "agent-secret" });

        job.Spec.Template.Spec.Containers[0].Env.Should().NotContain(e => e.Name.StartsWith("AGENT_CLAUDE_"));
    }

    // ── AgentSelector comma-to-dot conversion ────────────────────────────────

    [Fact]
    public void AgentSelectorLabel_CommasConvertedToDots()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(Guid.NewGuid()) with { AgentSelector = "dotnet,java,gpu" };

        var job = JobSpecBuilder.Build(template, ctx);

        var labelValue = job.Metadata.Labels["caa/agent-selector"];
        labelValue.Should().Be("dotnet.java.gpu",
            "commas in agent selector must be replaced with dots for K8s label validity");
    }

    // ── Resources: only Requests (no Limits) ────────────────────────────────

    [Fact]
    public void Build_WithOnlyRequests_NoLimits_LimitsIsNull()
    {
        var template = new JobTemplate
        {
            Labels = "dotnet",
            Image = "agent:latest",
            ProviderType = "generic",
            MaxConcurrent = 0,
            Resources = new JobTemplateResources
            {
                Requests = new Dictionary<string, string> { ["cpu"] = "100m", ["memory"] = "256Mi" },
                Limits = null
            }
        };
        var ctx = BaseCtx(Guid.NewGuid());

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Resources.Should().NotBeNull();
        container.Resources!.Requests.Should().ContainKey("cpu");
        container.Resources.Limits.Should().BeNull("Limits was not specified in template");
    }

    [Fact]
    public void Build_WithOnlyLimits_NoRequests_RequestsIsNull()
    {
        var template = new JobTemplate
        {
            Labels = "dotnet",
            Image = "agent:latest",
            ProviderType = "generic",
            MaxConcurrent = 0,
            Resources = new JobTemplateResources
            {
                Requests = null,
                Limits = new Dictionary<string, string> { ["cpu"] = "2", ["memory"] = "4Gi" }
            }
        };
        var ctx = BaseCtx(Guid.NewGuid());

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Resources.Should().NotBeNull();
        container.Resources!.Requests.Should().BeNull("Requests was not specified in template");
        container.Resources.Limits.Should().ContainKey("cpu");
    }

    // ── LOG_LEVEL propagation ────────────────────────────────────────────────

    [Fact]
    public void Build_WhenLogLevelSet_PropagatesLogLevel()
    {
        var original = Environment.GetEnvironmentVariable("LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", "Debug");
            var template = GenericTemplate();
            var ctx = BaseCtx(Guid.NewGuid());

            var job = JobSpecBuilder.Build(template, ctx);

            var env = job.Spec.Template.Spec.Containers[0].Env;
            env.Should().Contain(e => e.Name == "LOG_LEVEL" && e.Value == "Debug");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", original);
        }
    }

    [Fact]
    public void Build_WhenLogLevelNotSet_NoLogLevelEnvVar()
    {
        var original = Environment.GetEnvironmentVariable("LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", null);
            var template = GenericTemplate();
            var ctx = BaseCtx(Guid.NewGuid());

            var job = JobSpecBuilder.Build(template, ctx);

            var env = job.Spec.Template.Spec.Containers[0].Env;
            env.Should().NotContain(e => e.Name == "LOG_LEVEL");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOG_LEVEL", original);
        }
    }

    // ── OTEL env var propagation ─────────────────────────────────────────────

    [Fact]
    public void Build_WhenOtelEndpointSet_PropagatesOtelEndpoint()
    {
        var original = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318");
            var template = GenericTemplate();
            var ctx = BaseCtx(Guid.NewGuid());

            var job = JobSpecBuilder.Build(template, ctx);

            var env = job.Spec.Template.Spec.Containers[0].Env;
            env.Should().Contain(e => e.Name == "OTEL_EXPORTER_OTLP_ENDPOINT" && e.Value == "http://collector:4318");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", original);
        }
    }

    [Fact]
    public void Build_SetsOtelServiceNameToStableWorkerName()
    {
        // OTEL_SERVICE_NAME is always "coding-agent-worker" — not per-job.
        var template = GenericTemplate();
        var ctx = BaseCtx(Guid.NewGuid()) with { JobName = "caa-test-job" };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().Contain(e => e.Name == "OTEL_SERVICE_NAME" && e.Value == "coding-agent-worker");
    }

    [Fact]
    public void Build_SetsServiceInstanceIdAndJobNameInResourceAttributes()
    {
        var origAttrs = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
            var template = GenericTemplate();
            var ctx = BaseCtx(Guid.NewGuid()) with { JobName = "caa-test-job" };

            var job = JobSpecBuilder.Build(template, ctx);

            var env = job.Spec.Template.Spec.Containers[0].Env;
            var attrsEntry = env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
            attrsEntry.Should().NotBeNull("OTEL_RESOURCE_ATTRIBUTES must always be set");
            attrsEntry!.Value.Should().Contain("service.instance.id=caa-test-job");
            attrsEntry.Value.Should().Contain("k8s.job.name=caa-test-job");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", origAttrs);
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
    public void Build_WhenOtelResourceAttributesSet_ComposesResourceAttributes()
    {
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=production,service.namespace=coding-agent");
        try
        {
            var template = CreateTemplate();
            var ctx = CreateContext();
            ctx.JobName = "caa-abcdef12";

            var job = JobSpecBuilder.Build(template, ctx);

            var container = job.Spec.Template.Spec.Containers[0];
            var env = container.Env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
            env.Should().NotBeNull("OTEL_RESOURCE_ATTRIBUTES must always be set");
            // Parent value is preserved and new per-job attrs are appended
            env!.Value.Should().Contain("deployment.environment=production");
            env.Value.Should().Contain("service.namespace=coding-agent");
            env.Value.Should().Contain("service.instance.id=caa-abcdef12");
            env.Value.Should().Contain("k8s.job.name=caa-abcdef12");
            env.Value.Should().Contain("k8s.namespace.name=$(K8S_NAMESPACE_NAME)");
            env.Value.Should().Contain("k8s.pod.name=$(K8S_POD_NAME)");
            // Parent value must come first
            env.Value.Should().StartWith("deployment.environment=production");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
        }
    }

    [Fact]
    public void Build_WhenOtelVarsNotSet_DoesNotIncludeConditionallyPropagatedOnes()
    {
        // OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_EXPORTER_OTLP_PROTOCOL are conditional (only set when parent has them).
        // OTEL_RESOURCE_ATTRIBUTES is always set (service.instance.id + k8s attrs are unconditional).
        // TODO: Missing try/finally — env vars are set to null without restoring originals. If these vars
        // were set in the CI environment or by a prior test, subsequent tests will see null unexpectedly.
        // Fix: capture originals before the test and restore in a finally block (see
        // Build_WhenOtelResourceAttributesSet_ComposesResourceAttributes for the correct pattern).
        // See review finding (issue #2969).
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);

        var template = CreateTemplate();
        var ctx = CreateContext();
        ctx.JobName = "caa-abcdef12";

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_ENDPOINT").Should().BeNull();
        container.Env.FirstOrDefault(e => e.Name == "OTEL_EXPORTER_OTLP_PROTOCOL").Should().BeNull();
        // OTEL_RESOURCE_ATTRIBUTES is always present — starts directly with per-job attrs when parent is absent
        var resourceAttrs = container.Env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
        resourceAttrs.Should().NotBeNull("OTEL_RESOURCE_ATTRIBUTES must always be set with per-job identity attrs");
        resourceAttrs!.Value.Should().StartWith("service.instance.id=caa-abcdef12");
        resourceAttrs.Value.Should().Contain("k8s.job.name=caa-abcdef12");
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
    public void Build_SetsResourceAttributesEvenWhenParentHasNone()
    {
        // OTEL_RESOURCE_ATTRIBUTES is always set on the pod, even when the orchestrator has no value.
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
        var template = CreateTemplate();
        var ctx = CreateContext();
        ctx.JobName = "caa-abcdef12";

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env.FirstOrDefault(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
        env.Should().NotBeNull("OTEL_RESOURCE_ATTRIBUTES must always be set regardless of parent value");
        env!.Value.Should().StartWith("service.instance.id=caa-abcdef12",
            "when no parent value, composed attrs start with service.instance.id");
        env.Value.Should().Contain("k8s.job.name=caa-abcdef12");
        env.Value.Should().Contain("k8s.namespace.name=$(K8S_NAMESPACE_NAME)");
        env.Value.Should().Contain("k8s.pod.name=$(K8S_POD_NAME)");
    }

    [Fact]
    public void Build_DownwardApiVarsForNamespaceAndPodNamePrecedeResourceAttributes()
    {
        // K8S_NAMESPACE_NAME and K8S_POD_NAME must appear before OTEL_RESOURCE_ATTRIBUTES
        // in the env list — Kubernetes resolves $(VAR) substitution only for earlier entries.
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var envList = job.Spec.Template.Spec.Containers[0].Env;
        var nsIndex = envList.ToList().FindIndex(e => e.Name == "K8S_NAMESPACE_NAME");
        var podIndex = envList.ToList().FindIndex(e => e.Name == "K8S_POD_NAME");
        var attrsIndex = envList.ToList().FindIndex(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");

        nsIndex.Should().BeGreaterThan(-1, "K8S_NAMESPACE_NAME must be present via fieldRef");
        podIndex.Should().BeGreaterThan(-1, "K8S_POD_NAME must be present via fieldRef");
        attrsIndex.Should().BeGreaterThan(-1, "OTEL_RESOURCE_ATTRIBUTES must be present");
        nsIndex.Should().BeLessThan(attrsIndex, "K8S_NAMESPACE_NAME must precede OTEL_RESOURCE_ATTRIBUTES");
        podIndex.Should().BeLessThan(attrsIndex, "K8S_POD_NAME must precede OTEL_RESOURCE_ATTRIBUTES");

        // Verify fieldRef (not Value) on the Downward API entries
        var nsEnv = envList.First(e => e.Name == "K8S_NAMESPACE_NAME");
        nsEnv.Value.Should().BeNull("K8S_NAMESPACE_NAME must use valueFrom.fieldRef, not a literal Value");
        nsEnv.ValueFrom!.FieldRef!.FieldPath.Should().Be("metadata.namespace");

        var podEnv = envList.First(e => e.Name == "K8S_POD_NAME");
        podEnv.Value.Should().BeNull("K8S_POD_NAME must use valueFrom.fieldRef, not a literal Value");
        podEnv.ValueFrom!.FieldRef!.FieldPath.Should().Be("metadata.name");
    }

    // ── Job spec fields ───────────────────────────────────────────────────────

    [Fact]
    public void Build_JobSpecConstants_AreCorrect()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(Guid.NewGuid());

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Parallelism.Should().Be(1);
        job.Spec.Completions.Should().Be(1);
        job.Spec.BackoffLimit.Should().Be(2);
        job.Spec.TtlSecondsAfterFinished.Should().Be(3600);
    }

    [Fact]
    public void Build_Container_RestartPolicyIsNever()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(Guid.NewGuid());

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.RestartPolicy.Should().Be("Never");
        job.Spec.Template.Spec.TerminationGracePeriodSeconds.Should().Be(30);
    }

    // ── OrchestratorUrl env var ───────────────────────────────────────────────

    [Fact]
    public void Build_OrchestratorUrlEnvVar_SetFromContext()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(Guid.NewGuid()) with { OrchestratorUrl = "http://custom-orch:9090" };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().Contain(e => e.Name == "ORCHESTRATOR_URL" && e.Value == "http://custom-orch:9090");
    }

    // ── TraceParent env var (issue #2977) ────────────────────────────────────

    /// <summary>
    /// Acceptance Criterion 1 (issue #2977): the Job built for a WorkItem that has a TraceParent
    /// carries the TRACEPARENT env var with the exact value from BuildContext.TraceParent.
    /// </summary>
    [Fact]
    public void Build_WhenTraceParentSet_InjectsTraceparentEnvVar()
    {
        const string traceParentValue = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var ctx = BaseCtx(Guid.NewGuid()) with { TraceParent = traceParentValue };

        var job = JobSpecBuilder.Build(KiroTemplate(), ctx);

        var envVars = job.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().Contain(e => e.Name == "TRACEPARENT" && e.Value == traceParentValue,
            "TRACEPARENT env var must be injected with the exact value from BuildContext.TraceParent");
    }

    [Fact]
    public void Build_WhenTraceParentNull_DoesNotInjectTraceparentEnvVar()
    {
        // BaseCtx(Guid.NewGuid()) leaves TraceParent null (the property default).
        var ctx = BaseCtx(Guid.NewGuid());

        var job = JobSpecBuilder.Build(KiroTemplate(), ctx);

        var envVars = job.Spec.Template.Spec.Containers[0].Env;
        envVars.Should().NotContain(e => e.Name == "TRACEPARENT",
            "TRACEPARENT must not be present when BuildContext.TraceParent is null");
    }

    // ── Resources ───────────────────────────────────────────────────

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

    // ── PodSecurityContext ──────────────────────────────────────────

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

    // ── NodeSelector ────────────────────────────────────────────────

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

    // ── InitContainers ──────────────────────────────────────────────

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

    // ── Tolerations ─────────────────────────────────────────────────

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

    // ── Core Fields ─────────────────────────────────────────────────

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
    public void Build_SetsActiveDeadlineFromTimeout()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();
        ctx.TimeoutSeconds = 1800;

        var job = JobSpecBuilder.Build(template, ctx);

        // timeout + 60s buffer
        job.Spec.ActiveDeadlineSeconds.Should().Be(1860);
    }

    // ── PodSecurityContext — YAML round-trip ──────────────────────

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

        var provider = JobTemplateStore.LoadFromYaml(yaml);
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
        // the JsonElements produced by JobTemplateStore are compatible with
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

        var provider = JobTemplateStore.LoadFromYaml(yaml);
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

    // ── InitContainers VolumeMounts Injection ───────────────────────

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

    // ── AGENT_ID Env Var ────────────────────────────────────────────

    [Fact]
    public void Build_SetsAgentIdToJobName()
    {
        var template = CreateTemplate();
        var ctx = CreateContext();

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var agentIdEnv = container.Env.FirstOrDefault(e => e.Name == "AGENT_ID");
        agentIdEnv.Should().NotBeNull("AGENT_ID must be set for SignalR hub authentication");
        agentIdEnv!.Value.Should().Be(ctx.JobName,
            "the agent derives its API key as HMAC(masterKey, AGENT_ID) and DispatchLoop claims the " +
            "WorkItem with AssignedAgentId = job name, so the two must be the same string");
        agentIdEnv.ValueFrom.Should().BeNull(
            "the Downward API would supply metadata.name — the pod name, which carries a random " +
            "suffix and would never match the claimed AssignedAgentId");
    }

    // ── AGENT_LABELS Env Var ────────────────────────────────────────

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

    // ── Full Job Spec Validation (K8s API compliance) ───────────────

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

        var provider = JobTemplateStore.LoadFromYaml(yaml);
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

    // ── OpenCode Provider ───────────────────────────────────────────

    [Fact]
    public void Build_OpencodeAgent_WithConfigSecret_InjectsOpencodeConfigEnvVar()
    {
        // Spec 043/045: opencode config is injected via OPENCODE_CONFIG_CONTENT env var
        // sourced from the secret, not via a directory volume mount. entrypoint.sh writes
        // the file at startup. See JobSpecBuilder.BuildEnvVars for rationale.
        var template = CreateTemplate(providerType: "opencode", image: "chemsorly/coding-agent:opencode");
        var ctx = CreateContext(opcConfigSecret: "opencode-config-secret");

        var job = JobSpecBuilder.Build(template, ctx);

        // Env var must be present with SecretKeyRef pointing to the config secret
        var container = job.Spec.Template.Spec.Containers[0];
        var envVar = container.Env.FirstOrDefault(e => e.Name == "OPENCODE_CONFIG_CONTENT");
        envVar.Should().NotBeNull("OPENCODE_CONFIG_CONTENT must be injected when OpencodeConfigSecretName is set");
        envVar!.ValueFrom.Should().NotBeNull();
        envVar.ValueFrom.SecretKeyRef.Should().NotBeNull();
        envVar.ValueFrom.SecretKeyRef.Name.Should().Be("opencode-config-secret");
        envVar.ValueFrom.SecretKeyRef.Key.Should().Be("opencode-config-content");

        // No volume mount — directory volume mounts break entrypoint.sh's ability to write the file
        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "opencode-config");
        container.VolumeMounts.Should().NotContain(vm => vm.Name == "opencode-config");
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

    // ── ProjectSecrets Boundary ─────────────────────────────────────

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
}
