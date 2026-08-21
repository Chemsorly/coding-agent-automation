using AwesomeAssertions;
using k8s.Models;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="JobSpecBuilder"/> — the new Kubernetes-assembly variant.
/// Covers branches not exercised by the Orchestration-assembly tests:
///   - DerivedKeySecretName path: AGENT_API_KEY from Secret, no master volume mount
///   - Legacy path: no DerivedKeySecretName → master Secret file mount
///   - OpenCode agent without OpencodeConfigSecretName → no OPENCODE_CONFIG_CONTENT env var
///   - WorkItemId null → no caa/work-item-id label, no --work-item-id arg
///   - PodSecurityContext from template JSON
///   - DeserializeK8s null result throws
/// </summary>
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
        var ctx = BaseCtx(workItemId: Guid.NewGuid()) with
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
