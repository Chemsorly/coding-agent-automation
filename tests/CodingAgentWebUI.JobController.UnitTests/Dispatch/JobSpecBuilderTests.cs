using AwesomeAssertions;
using k8s.Models;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

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

    // ── DerivedKeySecretName path ────────────────────────────────────────────

    [Fact]
    public void WhenDerivedKeySecretName_Set_AgentApiKeyEnvVar_FromSecret_NoMasterMount()
    {
        var template = KiroTemplate();
        var ctx = BaseCtx(workItemId: null) with
        {
            DerivedKeySecretName = "caa-derived-abc123"
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var env = container.Env;

        // AGENT_API_KEY must be from SecretKeyRef, not AGENT_API_KEY_FILE
        var apiKeyEnv = env.SingleOrDefault(e => e.Name == "AGENT_API_KEY");
        apiKeyEnv.Should().NotBeNull("AGENT_API_KEY must be set via SecretKeyRef");
        apiKeyEnv!.ValueFrom.Should().NotBeNull();
        apiKeyEnv.ValueFrom!.SecretKeyRef!.Name.Should().Be("caa-derived-abc123");
        apiKeyEnv.ValueFrom.SecretKeyRef.Key.Should().Be("agent-api-key");

        // AGENT_API_KEY_FILE must NOT be present
        env.Should().NotContain(e => e.Name == "AGENT_API_KEY_FILE",
            "derived key path must not emit AGENT_API_KEY_FILE");

        // Master agent-api-key volume must NOT be mounted
        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().NotContain(v => v.Name == "agent-api-key",
            "derived-key jobs must not mount master agent-api-key Secret");
    }

    [Fact]
    public void Build_WhenDerivedKeySecretNameSetForWorkItemPod_ShouldThrow()
    {
        // Guard: DerivedKeySecretName + WorkItemId together → double-derivation footgun.
        var ctx = BaseCtx(workItemId: Guid.NewGuid()) with
        {
            DerivedKeySecretName = "caa-derived-abc123"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => JobSpecBuilder.Build(KiroTemplate(), ctx));
        ex.Message.Should().Contain("double-derivation");
        ex.Message.Should().Contain("decisions.md");
        ex.Message.Should().Contain(ctx.WorkItemId.ToString()!);
    }

    // ── Legacy path (no DerivedKeySecretName) ────────────────────────────────

    [Fact]
    public void WhenDerivedKeySecretName_Null_AgentApiKeyFile_Env_AndMasterMount()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx() with
        {
            DerivedKeySecretName = null
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];
        var env = container.Env;

        // AGENT_API_KEY_FILE must be present
        env.Should().Contain(e => e.Name == "AGENT_API_KEY_FILE");

        // AGENT_API_KEY (env-var form) must NOT be present
        env.Should().NotContain(e => e.Name == "AGENT_API_KEY");

        // Master volume must be mounted
        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "agent-api-key");
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

    [Fact]
    public void WorkItem_EmptyProjectSecrets_NoProjectSecretsVolume()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: Guid.NewGuid()) with
        {
            ProjectSecrets = new Dictionary<string, string>()
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

    // ── AGENT_ID equals JobName ──────────────────────────────────────────────

    [Fact]
    public void AgentIdEnvVar_EqualsJobName()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx() with { JobName = "caa-specific-job-name" };

        var job = JobSpecBuilder.Build(template, ctx);

        var agentIdEnv = job.Spec.Template.Spec.Containers[0].Env
            .Single(e => e.Name == "AGENT_ID");
        agentIdEnv.Value.Should().Be("caa-specific-job-name");
    }
}

// ─── Additional coverage tests ────────────────────────────────────────────────

public sealed class JobSpecBuilderAdditionalTests
{
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

    private static JobTemplate KiroTemplate() => new()
    {
        Labels = "dotnet,kiro",
        Image = "agent:latest",
        ProviderType = "kiro",
        MaxConcurrent = 2
    };

    private static JobSpecBuilder.BuildContext BaseCtx(Guid? workItemId = null) => new()
    {
        WorkItemId = workItemId ?? Guid.NewGuid(),
        AgentSelector = "dotnet,kiro",
        TimeoutSeconds = 3600,
        JobName = "caa-test-job",
        ClaimedPvc = null,
        OrchestratorUrl = "http://orchestrator:5000",
        AgentApiKeySecretName = "agent-api-key",
        AgentServiceAccountName = "agent-sa",
        Namespace = "default"
    };

    // ── OpenCode WITH config secret ───────────────────────────────────────────

    [Fact]
    public void OpenCode_WithConfigSecretName_InjectsOpencodeConfigContentEnvVar()
    {
        var template = OpenCodeTemplate();
        var ctx = BaseCtx() with
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
        var ctx = BaseCtx() with
        {
            OpencodeConfigSecretName = "opencode-config-secret"
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().NotContain(e => e.Name == "OPENCODE_CONFIG_CONTENT",
            "only opencode agents get OPENCODE_CONFIG_CONTENT");
    }

    // ── AgentSelector comma-to-dot conversion ────────────────────────────────

    [Fact]
    public void AgentSelectorLabel_CommasConvertedToDots()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx() with { AgentSelector = "dotnet,java,gpu" };

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
        var ctx = BaseCtx();

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
        var ctx = BaseCtx();

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
            var ctx = BaseCtx();

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
            var ctx = BaseCtx();

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
            var ctx = BaseCtx();

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
    public void Build_WhenOtelVarsNotSet_NoOptionalOtelEnvVars()
    {
        var origEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var origProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        var origAttrs = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);

            var template = GenericTemplate();
            var ctx = BaseCtx();

            var job = JobSpecBuilder.Build(template, ctx);

            var env = job.Spec.Template.Spec.Containers[0].Env;
            env.Should().NotContain(e => e.Name == "OTEL_EXPORTER_OTLP_ENDPOINT");
            env.Should().NotContain(e => e.Name == "OTEL_EXPORTER_OTLP_PROTOCOL");
            env.Should().NotContain(e => e.Name == "OTEL_RESOURCE_ATTRIBUTES");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", origEndpoint);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", origProtocol);
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", origAttrs);
        }
    }

    // ── ProjectSecrets with workItemId ────────────────────────────────────────

    [Fact]
    public void WorkItem_WithProjectSecrets_ProjectSecretsVolumeMounted()
    {
        var id = Guid.NewGuid();
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: id) with
        {
            ProjectSecrets = new Dictionary<string, string> { ["DB_PASS"] = "secret" }
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes.Should().Contain(v => v.Name == "project-secrets");

        var mounts = job.Spec.Template.Spec.Containers[0].VolumeMounts;
        mounts.Should().Contain(m => m.Name == "project-secrets");
    }

    [Fact]
    public void WorkItem_WithProjectSecrets_SecretNameDerivedFromWorkItemId()
    {
        var id = Guid.NewGuid();
        var template = GenericTemplate();
        var ctx = BaseCtx(workItemId: id) with
        {
            ProjectSecrets = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "token" }
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var volume = job.Spec.Template.Spec.Volumes!.Single(v => v.Name == "project-secrets");
        var expectedSecretName = $"caa-secrets-{id.ToString("N")[..8]}";
        volume.Secret!.SecretName.Should().Be(expectedSecretName);
        volume.Secret.Optional.Should().BeTrue();
    }

    // ── Non-kiro agent: no kiro-cli-data volume even with PVC ────────────────

    [Fact]
    public void NonKiroAgent_WithPvc_NoKiroCliDataVolume()
    {
        // Non-kiro (opencode) agent should not get kiro-cli-data volume
        var template = OpenCodeTemplate();
        var ctx = BaseCtx() with { ClaimedPvc = "pvc-1" };

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes;
        volumes?.Should().NotContain(v => v.Name == "kiro-cli-data",
            "only kiro agents get the kiro-cli-data PVC mount");
    }

    // ── Job spec fields ───────────────────────────────────────────────────────

    [Fact]
    public void Build_JobSpecConstants_AreCorrect()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx();

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
        var ctx = BaseCtx();

        var job = JobSpecBuilder.Build(template, ctx);

        job.Spec.Template.Spec.RestartPolicy.Should().Be("Never");
        job.Spec.Template.Spec.TerminationGracePeriodSeconds.Should().Be(30);
    }

    // ── OrchestratorUrl env var ───────────────────────────────────────────────

    [Fact]
    public void Build_OrchestratorUrlEnvVar_SetFromContext()
    {
        var template = GenericTemplate();
        var ctx = BaseCtx() with { OrchestratorUrl = "http://custom-orch:9090" };

        var job = JobSpecBuilder.Build(template, ctx);

        var env = job.Spec.Template.Spec.Containers[0].Env;
        env.Should().Contain(e => e.Name == "ORCHESTRATOR_URL" && e.Value == "http://custom-orch:9090");
    }
}
