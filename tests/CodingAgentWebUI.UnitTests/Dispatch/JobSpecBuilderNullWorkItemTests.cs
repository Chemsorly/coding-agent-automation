using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for nullable <see cref="JobSpecBuilder.BuildContext.WorkItemId"/> behavior.
/// Requirements: Req 1 — when WorkItemId is null, --work-item-id arg omitted,
/// caa/work-item-id label omitted, ProjectSecrets volume NOT mounted.
/// </summary>
public class JobSpecBuilderNullWorkItemTests
{
    private static JobTemplate CreateKiroTemplate() => new JobTemplate
    {
        Labels = "dotnet,kiro",
        Image = "chemsorly/coding-agent:kiro-dotnet10",
        ProviderType = "kiro",
        MaxConcurrent = 2
    };

    private static JobSpecBuilder.BuildContext CreateContext(
        Guid? workItemId,
        Dictionary<string, string>? projectSecrets = null,
        string? claimedPvc = null) =>
        new JobSpecBuilder.BuildContext
        {
            WorkItemId = workItemId,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 1800,
            JobName = "caa-chat-abcd1234",
            ClaimedPvc = claimedPvc,
            OrchestratorUrl = "http://orchestrator:8080",
            AgentApiKeySecretName = "caa-secret",
            AgentServiceAccountName = "caa-agent",
            Namespace = "coding-agent",
            ProjectSecrets = projectSecrets
        };

    // ── Arg tests ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_WorkItemIdNull_ContainerArgsDoesNotContainWorkItemIdArg()
    {
        var template = CreateKiroTemplate();
        var ctx = CreateContext(workItemId: null);

        var job = JobSpecBuilder.Build(template, ctx);

        var args = job.Spec.Template.Spec.Containers[0].Args ?? [];
        args.Should().NotContain(a => a.StartsWith("--work-item-id"),
            "chat pods must not emit --work-item-id when WorkItemId is null");
    }

    [Fact]
    public void Build_WorkItemIdNonNull_ContainerArgsContainsWorkItemIdArg()
    {
        var id = Guid.NewGuid();
        var template = CreateKiroTemplate();
        var ctx = CreateContext(workItemId: id);

        var job = JobSpecBuilder.Build(template, ctx);

        var args = job.Spec.Template.Spec.Containers[0].Args ?? [];
        args.Should().Contain($"--work-item-id={id}",
            "non-null WorkItemId must produce the --work-item-id arg (regression guard)");
    }

    // ── Label tests ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_WorkItemIdNull_JobLabelsDoNotContainWorkItemIdLabel()
    {
        var template = CreateKiroTemplate();
        var ctx = CreateContext(workItemId: null);

        var job = JobSpecBuilder.Build(template, ctx);

        job.Metadata.Labels.Should().NotContainKey("caa/work-item-id",
            "chat pods must not emit caa/work-item-id label when WorkItemId is null");
    }

    [Fact]
    public void Build_WorkItemIdNonNull_JobLabelsContainWorkItemIdLabel()
    {
        var id = Guid.NewGuid();
        var template = CreateKiroTemplate();
        var ctx = CreateContext(workItemId: id);

        var job = JobSpecBuilder.Build(template, ctx);

        job.Metadata.Labels.Should().ContainKey("caa/work-item-id",
            "non-null WorkItemId must produce the caa/work-item-id label (regression guard)");
        job.Metadata.Labels["caa/work-item-id"].Should().Be(id.ToString());
    }

    // ── ProjectSecrets volume tests ──────────────────────────────────────────

    [Fact]
    public void Build_WorkItemIdNull_ProjectSecretsNonNull_ProjectSecretsVolumeNotMounted()
    {
        var template = CreateKiroTemplate();
        var secrets = new Dictionary<string, string> { ["SECRET_KEY"] = "secret-value" };
        var ctx = CreateContext(workItemId: null, projectSecrets: secrets);

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes ?? [];
        volumes.Should().NotContain(v => v.Name == "project-secrets",
            "WorkItemId=null must suppress the project-secrets volume even when ProjectSecrets is non-null");

        var mounts = job.Spec.Template.Spec.Containers[0].VolumeMounts ?? [];
        mounts.Should().NotContain(m => m.Name == "project-secrets",
            "WorkItemId=null must suppress the project-secrets volume mount even when ProjectSecrets is non-null");
    }

    [Fact]
    public void Build_WorkItemIdNonNull_ProjectSecretsNonNull_ProjectSecretsVolumeMounted()
    {
        var id = Guid.NewGuid();
        var template = CreateKiroTemplate();
        var secrets = new Dictionary<string, string> { ["SECRET_KEY"] = "secret-value" };
        var ctx = CreateContext(workItemId: id, projectSecrets: secrets);

        var job = JobSpecBuilder.Build(template, ctx);

        var volumes = job.Spec.Template.Spec.Volumes ?? [];
        volumes.Should().Contain(v => v.Name == "project-secrets",
            "non-null WorkItemId with ProjectSecrets must mount the project-secrets volume (regression guard)");
    }
}
