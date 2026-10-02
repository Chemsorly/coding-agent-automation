using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests verifying that project-level settings reach the <see cref="JobAssignmentMessage"/>
/// that the agent fetches via <c>GET /api/work-items/{id}/assignment</c>.
///
/// <para>
/// Issue #3102. Each test corresponds to one of the six scenarios described in the issue:
/// <list type="number">
///   <item>Behavioural override (MaxRetries) — non-null project setting replaces global default.</item>
///   <item>Deep merge (CodeReview) — partial project override leaves other sub-fields at global values.</item>
///   <item>Snapshot timing — config change made while WorkItem is Pending is visible at assignment time.</item>
///   <item>MCP server merge — profile + project servers are merged; Disabled flag is preserved.</item>
///   <item>Secret merge — project secrets and repository secrets arrive in separate assignment fields.</item>
///   <item>Template BrainReadOnly — template flag overrides project flag one-directionally (only to true).</item>
/// </list>
/// A seventh scenario covers the project review: its reviewer and the project's other repository reach the
/// assignment.
/// </para>
///
/// <para>
/// All assertions target the <see cref="JobAssignmentMessage"/> captured by
/// <see cref="FakeAgentClient.JobAssigned"/> — the object the agent actually receives after
/// calling the assignment endpoint — never internal service state.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "ProjectSettings")]
[Collection(E2ECollection.Name)]
public sealed class DbModeProjectSettingsTests : HeadlessE2ETestBase
{
    // ── Issue identifiers ────────────────────────────────────────────────────────
    // Identifiers used in other files: 1, 10, 14, 42–51, 55, 60–62, 77, 80–82, 92, 99,
    // 100–103, 200, 300, 700, 3000, 3010, 3011. Identifiers 49, 52–54, 56–59 and 63 are unallocated
    // and are used here. ("55" is taken by PrReviewPipelineTests.)

    public DbModeProjectSettingsTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1 — Behavioural override: a non-null project setting replaces the global
    /// default; when the override is absent the global value applies.
    ///
    /// <para>
    /// Uses two separate dispatches with distinct issue identifiers to avoid the dedup guard
    /// in <c>DispatchOrchestrationService</c> (which rejects a second dispatch for the same
    /// identifier while the first WorkItem is still active).
    /// </para>
    /// </summary>
    [Fact]
    public async Task DbMode_ProjectOverride_MaxRetries_ReplacesGlobalDefault()
    {
        // Global default is MaxRetries = 3 (set in InMemoryConfigurationStore.SeedDefaults).
        const string overrideProjectId = "aaaaaaaa-bbbb-cccc-dddd-111111111111";

        // Seed a project with a MaxRetries override.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = overrideProjectId,
            Name = "Override Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            MaxRetries = 5
        }, CancellationToken.None);

        // Seed template + profile + issues.
        await Fixture.ConfigStore.SaveTemplateAsync(overrideProjectId, new PipelineJobTemplate
        {
            Id = "template-override-e2e",
            Name = "Override Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-override-e2e",
            DisplayName = "Override Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Issue "52": dispatched with the project that has MaxRetries = 5.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "52",
            Title = "Override test — with override",
            Description = "## Requirements\nTest MaxRetries override\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Issue "53": dispatched against the Default project (MaxRetries = null → falls back to 3).
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "53",
            Title = "Override test — no override",
            Description = "## Requirements\nTest global default\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // TODO [WARNING]: This test dispatches issue "53" to the Default project (which never had an
        // override) to prove global fallback. The issue scenario describes "clearing the override
        // (back to 'Using global default') gives 10 on the next dispatch" — i.e., mutating the same
        // project back to null MaxRetries and re-dispatching. These are related but distinct code
        // paths; the current structure tests a project-with-no-override, not a
        // previously-overridden-project whose override was cleared. Consider adding a third dispatch
        // against the same overrideProjectId after setting MaxRetries = null to cover that path.

        // Also seed the default-project template and profile so the second dispatch succeeds.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-override-default-e2e",
            Name = "Override Default Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient("override-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // ── First dispatch: with override ──────────────────────────────────────
        var result1 = await DispatchIssueAsync("52", projectId: overrideProjectId);
        Assert.True(result1.Success, $"First dispatch failed: {result1.ErrorMessage}");

        var assignment1 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, assignment1.PipelineConfiguration.MaxRetries);

        // Complete the first job so the agent is free and the dedup guard clears for the next.
        await agent.AcceptAndCompleteJobAsync(assignment1.JobId);

        // Wait for the first WorkItem to reach a terminal state before dispatching again.
        await WaitForWorkItemStatusAsync(
            Guid.Parse(result1.WorkItemId!), WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));

        // ── Second dispatch: no override (Default project) ─────────────────────
        // TODO [WARNING]: ResetJobAssigned() is called here (after WaitForWorkItemStatusAsync) to
        // ensure the TCS is fresh before the second dispatch. The ordering is correct but fragile:
        // if a future edit dispatches the second issue before this reset, or if any retry/requeue
        // path re-enqueues issue "52", FakeJobController could TrySetResult on the stale TCS,
        // causing assignment2 to hang until the 10-second timeout fires with the wrong assignment.
        // Calling ResetJobAssigned() immediately after WaitForWorkItemStatusAsync returns (before any
        // other awaits) is the safe pattern; verify this ordering is preserved on any future edits.
        agent.ResetJobAssigned();

        var result2 = await DispatchIssueAsync("53");
        Assert.True(result2.Success, $"Second dispatch failed: {result2.ErrorMessage}");

        var assignment2 = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, assignment2.PipelineConfiguration.MaxRetries);

        await agent.AcceptAndCompleteJobAsync(assignment2.JobId);
    }

    // ── Scenario 2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 2 — Deep merge: a partial <c>CodeReview</c> project override replaces only
    /// the sub-fields it sets; other sub-fields retain their global values.
    /// </summary>
    [Fact]
    public async Task DbMode_ProjectOverride_CodeReview_DeepMerge_LeavesOtherSubFieldsAtGlobalDefault()
    {
        const string deepMergeProjectId = "aaaaaaaa-bbbb-cccc-dddd-222222222222";

        // Override only MaxIterations; FixPrompt and InlineComments are left null (inherit global).
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = deepMergeProjectId,
            Name = "Deep Merge Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            CodeReview = new CodeReviewOverrides { MaxIterations = 0 }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(deepMergeProjectId, new PipelineJobTemplate
        {
            Id = "template-deepmerge-e2e",
            Name = "Deep Merge Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-deepmerge-e2e",
            DisplayName = "Deep Merge Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "54",
            Title = "Deep merge test",
            Description = "## Requirements\nTest CodeReview deep merge\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("deepmerge-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("54", projectId: deepMergeProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Overridden sub-field:
        Assert.Equal(0, assignment.PipelineConfiguration.CodeReview.MaxIterations);

        // Non-overridden sub-fields retain their global defaults:
        // FixPrompt global default is null.
        // TODO [WARNING]: This assertion is too weak to prove deep-merge. Assert.Null(FixPrompt)
        // passes whether deep-merge correctly left the global null value or whether the merge code
        // wiped the entire CodeReview object (both produce null). To make the assertion meaningful,
        // seed a non-null global default for a second CodeReview sub-field (e.g., InlineComments.Enabled
        // defaults to true) and assert it survives the partial override — Assert.True(
        // assignment.PipelineConfiguration.CodeReview.InlineComments.Enabled) would fail if the
        // merge replaces rather than merges. Also consider using MaxIterations = 99 instead of 0
        // so the override is unambiguous (0 could be treated as "not set" by a resolver using
        // falsy checks).
        Assert.Null(assignment.PipelineConfiguration.CodeReview.FixPrompt);

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    // ── Scenario 3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 3 — Snapshot timing: a project override changed while the WorkItem is
    /// Pending (after dispatch but before an agent claims it) is visible in the assignment.
    /// The assignment is resolved fresh at claim time, not frozen in the payload.
    /// </summary>
    [Fact]
    public async Task DbMode_ProjectOverride_ChangedWhilePending_AssignmentReflectsNewValue()
    {
        const string snapshotProjectId = "aaaaaaaa-bbbb-cccc-dddd-333333333333";

        // Seed project with initial MaxRetries = 5.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = snapshotProjectId,
            Name = "Snapshot Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            MaxRetries = 5
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(snapshotProjectId, new PipelineJobTemplate
        {
            Id = "template-snapshot-e2e",
            Name = "Snapshot Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-snapshot-e2e",
            DisplayName = "Snapshot Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "56",
            Title = "Snapshot timing test",
            Description = "## Requirements\nTest snapshot timing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        // Dispatch WITHOUT a connected agent — WorkItem goes to Pending.
        var result = await DispatchIssueAsync("56", projectId: snapshotProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Mutate the project override BEFORE the agent connects (while WorkItem is Pending).
        // SaveProjectAsync replaces the existing project in-memory; the assignment enricher
        // reads it fresh when the agent fetches the assignment.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = snapshotProjectId,
            Name = "Snapshot Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            MaxRetries = 7
        }, CancellationToken.None);

        // Now connect the agent — FakeJobController will claim the Pending item and dispatch it.
        await using var agent = new FakeAgentClient("snapshot-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Wait for the work item to be dispatched to the agent.
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(15));

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // The assignment must reflect the UPDATED value (7), not the original dispatch-time value (5).
        Assert.Equal(7, assignment.PipelineConfiguration.MaxRetries);

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    // ── Scenario 4 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 4 — MCP server merge: project servers are merged with profile servers using
    /// project-wins-on-collision semantics. A project server with <c>Disabled = true</c> is
    /// present in the merged list with the flag preserved — filtering disabled servers is the
    /// agent's responsibility, not the assignment endpoint's.
    /// </summary>
    [Fact]
    public async Task DbMode_McpServerMerge_ProfileAndProjectServersAreMerged_DisabledFlagPreserved()
    {
        const string mcpProjectId = "aaaaaaaa-bbbb-cccc-dddd-444444444444";

        // Profile has servers "a" (http) and "b" (http) at a profile URL.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-mcp-e2e",
            DisplayName = "MCP Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true,
            McpServers = new[]
            {
                new McpServerConfig { Name = "a", Type = "http", Url = "http://profile-a" },
                new McpServerConfig { Name = "b", Type = "http", Url = "http://profile-b" }
            }
        }, CancellationToken.None);

        // Project overrides "b" with a different URL, adds "c", and adds "disabled-server" (Disabled=true).
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = mcpProjectId,
            Name = "MCP Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            McpServers = new[]
            {
                new McpServerConfig { Name = "b", Type = "http", Url = "http://project-b" },
                new McpServerConfig { Name = "c", Type = "http", Url = "http://project-c" },
                new McpServerConfig { Name = "disabled-server", Type = "http", Url = "http://disabled", Disabled = true }
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(mcpProjectId, new PipelineJobTemplate
        {
            Id = "template-mcp-e2e",
            Name = "MCP Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "57",
            Title = "MCP merge test",
            Description = "## Requirements\nTest MCP server merge\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("mcp-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("57", projectId: mcpProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Expect exactly 4 servers: a, b (project URL), c, disabled-server.
        Assert.Equal(4, assignment.McpServers.Count);

        // "a" — from profile only, unchanged.
        var serverA = assignment.McpServers.FirstOrDefault(s => s.Name == "a");
        Assert.NotNull(serverA);
        Assert.False(serverA.Disabled);
        // TODO [WARNING]: serverA.Url is not verified. If the merge swaps server contents or
        // duplicates profile-b under the name "a", the URL check would catch it. Consider adding:
        // Assert.Equal("http://profile-a", serverA.Url);

        // "b" — project wins on name collision; URL must be the project's.
        var serverB = assignment.McpServers.FirstOrDefault(s => s.Name == "b");
        Assert.NotNull(serverB);
        Assert.Equal("http://project-b", serverB.Url);
        Assert.False(serverB.Disabled);

        // "c" — added by project.
        var serverC = assignment.McpServers.FirstOrDefault(s => s.Name == "c");
        Assert.NotNull(serverC);
        Assert.False(serverC.Disabled);
        // TODO [WARNING]: serverC.Url is not verified. If the merge drops or mutates the URL,
        // the test passes silently. Consider adding: Assert.Equal("http://project-c", serverC.Url);

        // "disabled-server" — present in the assignment with Disabled=true.
        // McpServerMerge.Merge carries the flag through; filtering is the agent's responsibility.
        var serverDisabled = assignment.McpServers.FirstOrDefault(s => s.Name == "disabled-server");
        Assert.NotNull(serverDisabled);
        Assert.True(serverDisabled.Disabled);

        // TODO [WARNING]: mid-assertion orphaned work item — if any of the assertions above throw,
        // AcceptAndCompleteJobAsync is never called. The work item remains in Dispatched state and
        // may interfere with other tests in the same E2ECollection. This structural weakness is
        // shared with all other scenarios in this file. A try/finally or a fixture-level cleanup
        // helper would guard against it.
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    // ── Scenario 5 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 5 — Secret merge: project secrets and repository secrets arrive in separate
    /// fields of the assignment. The actual merge (repo wins on key collision) happens on the
    /// agent side in <c>RunEnvironmentSetupStep</c>, not in the API.
    /// </summary>
    [Fact]
    public async Task DbMode_SecretMerge_ProjectAndRepoSecretsDeliveredInSeparateFields()
    {
        const string secretsProjectId = "aaaaaaaa-bbbb-cccc-dddd-555555555555";

        // Update the existing "repo-e2e" provider config to add a repo-level secret K2=r.
        // Must UPDATE (not add a new config) — DispatchIssueAsync defaults repoProviderId="repo-e2e"
        // and adding a different ID would break dispatch.
        // TODO [WARNING]: This mutates the shared "repo-e2e" provider config without restoring it
        // after the test. Because all tests share the same E2EFixture (xUnit [Collection]), any
        // test running after this one that relies on "repo-e2e" having no secrets (or different
        // secrets) will observe the contaminated state. Add a teardown step (e.g., call
        // SaveProviderConfigAsync again with Secrets = null) after the test body, or use a
        // try/finally block to ensure cleanup even on assertion failures.
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-e2e",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "E2E Repo Provider",
            Secrets = new Dictionary<string, string> { ["K2"] = "r" }
        }, CancellationToken.None);

        // Seed project with K1=p and K2=p.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = secretsProjectId,
            Name = "Secrets Merge Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            Secrets = new Dictionary<string, string>
            {
                ["K1"] = "p",
                ["K2"] = "p"
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(secretsProjectId, new PipelineJobTemplate
        {
            Id = "template-secrets-merge-e2e",
            Name = "Secrets Merge Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-secrets-merge-e2e",
            DisplayName = "Secrets Merge Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "58",
            Title = "Secret merge test",
            Description = "## Requirements\nTest secret merge\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("secrets-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("58", projectId: secretsProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Project secrets arrive in JobAssignmentMessage.ProjectSecrets.
        Assert.NotNull(assignment.ProjectSecrets);
        Assert.Equal("p", assignment.ProjectSecrets["K1"]);
        Assert.Equal("p", assignment.ProjectSecrets["K2"]);

        // Repo-level secrets arrive in ProviderConfig.Secrets for the repo provider.
        var repoConfig = assignment.ProviderConfigs.FirstOrDefault(c => c.Id == "repo-e2e");
        Assert.NotNull(repoConfig);
        Assert.NotNull(repoConfig.Secrets);
        Assert.Equal("r", repoConfig.Secrets["K2"]);
        // TODO [WARNING]: K1 is not asserted absent from repoConfig.Secrets. If the implementation
        // incorrectly copies all project secrets into the repo provider config, this test still
        // passes because the assertions only verify presence of K2=r, not absence of K1. Add:
        // Assert.False(repoConfig.Secrets.ContainsKey("K1"), "K1 must not bleed into repo secrets");

        // The merge (repo K2 wins over project K2) happens on the agent side — not asserted here.

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    // ── Scenario 6 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 6a — Template BrainReadOnly: a template with <c>BrainReadOnly = true</c> forces
    /// the assignment's pipeline config to read-only even when the project has
    /// <c>BrainReadOnly = false</c> (or null).
    /// </summary>
    [Fact]
    public async Task DbMode_TemplateBrainReadOnly_True_ForcesReadOnlyInAssignment()
    {
        const string brainProjectId = "aaaaaaaa-bbbb-cccc-dddd-666666666666";

        // Project has BrainReadOnly = false (explicit); template overrides to true.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = brainProjectId,
            Name = "BrainReadOnly Project A",
            Enabled = true,
            TemplateIds = new List<string>(),
            BrainReadOnly = false
        }, CancellationToken.None);

        // Template with BrainReadOnly = true; RepoProviderId = "repo-e2e" so ApplyTemplateOverrides matches.
        await Fixture.ConfigStore.SaveTemplateAsync(brainProjectId, new PipelineJobTemplate
        {
            Id = "template-brain-ro-true-e2e",
            Name = "BrainReadOnly True Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainReadOnly = true,
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-brain-ro-e2e",
            DisplayName = "BrainReadOnly Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "59",
            Title = "BrainReadOnly true test",
            Description = "## Requirements\nTest BrainReadOnly template override\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("brain-ro-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("59", projectId: brainProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Template BrainReadOnly=true must override project BrainReadOnly=false → true.
        Assert.True(assignment.PipelineConfiguration.BrainReadOnly);

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    /// <summary>
    /// Scenario 6b — Template BrainReadOnly one-directional: a template with
    /// <c>BrainReadOnly = false</c> does NOT flip a project with <c>BrainReadOnly = true</c>
    /// back to false. The flag only moves in one direction.
    /// </summary>
    [Fact]
    public async Task DbMode_TemplateBrainReadOnly_False_DoesNotOverrideProjectTrue()
    {
        const string brainProjectId2 = "aaaaaaaa-bbbb-cccc-dddd-777777777777";

        // Project has BrainReadOnly = true.
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = brainProjectId2,
            Name = "BrainReadOnly Project B",
            Enabled = true,
            TemplateIds = new List<string>(),
            BrainReadOnly = true
        }, CancellationToken.None);

        // Template has BrainReadOnly = false (default) — must NOT flip project's true back to false.
        await Fixture.ConfigStore.SaveTemplateAsync(brainProjectId2, new PipelineJobTemplate
        {
            Id = "template-brain-ro-false-e2e",
            Name = "BrainReadOnly False Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainReadOnly = false,
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-brain-ro2-e2e",
            DisplayName = "BrainReadOnly B Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "49",
            Title = "BrainReadOnly false-does-not-override test",
            Description = "## Requirements\nTest BrainReadOnly one-directional\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("brain-ro2-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("49", projectId: brainProjectId2);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Project BrainReadOnly=true must survive; template BrainReadOnly=false is a no-op.
        Assert.True(assignment.PipelineConfiguration.BrainReadOnly);

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }

    // ── Scenario 7 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 7 — Project review: with the project review on, the assignment carries the project's reviewer and
    /// the project's other repository, but not the run's own one. The other repository is a GitLab repository, so it
    /// arrives as a clone-only config: its own token, none of its secrets.
    /// </summary>
    [Fact]
    public async Task DbMode_ProjectReview_AssignmentCarriesTheReviewerAndTheOtherRepositoryToClone()
    {
        const string reviewProjectId = "aaaaaaaa-bbbb-cccc-dddd-888888888888";

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-e2e-project-review-other",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Other Product Repository",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-e2e-other",
                ["projectId"] = "4401"
            },
            Secrets = new Dictionary<string, string> { ["OTHER_SECRET"] = "must-not-reach-the-job" }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = reviewProjectId,
            Name = "Project Review Project",
            Enabled = true,
            TemplateIds = new List<string>(),
            ProjectReviewEnabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(reviewProjectId, new PipelineJobTemplate
        {
            Id = "template-project-review-own-e2e",
            Name = "Own Repository",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(reviewProjectId, new PipelineJobTemplate
        {
            Id = "template-project-review-other-e2e",
            Name = "Other Repository",
            IssueProviderId = "issue-e2e-project-review-other",
            RepoProviderId = "repo-e2e-project-review-other",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-project-review-e2e",
            DisplayName = "Project Review Agent Profile",
            MatchLabels = new[] { "db-e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "63",
            Title = "Project review test",
            Description = "## Requirements\nTest the project review\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        await using var agent = new FakeAgentClient("project-review-agent-1", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("63", projectId: reviewProjectId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var reviewer = Assert.Single(assignment.ProjectReviewers);
        Assert.Equal(PipelineConfigurationDefaults.DefaultProjectReviewerName, reviewer.Name);
        Assert.Equal(PipelineConfigurationDefaults.DefaultProjectReviewPrompt, reviewer.Prompt);

        var other = Assert.Single(assignment.ProjectReviewRepositories!);
        Assert.Equal("Other Repository", other.TemplateName);
        Assert.Equal("repo-e2e-project-review-other", other.RepoProviderId);

        var cloneConfig = Assert.Single(assignment.ProviderConfigs, c => c.Id == "repo-e2e-project-review-other");
        Assert.Equal("glpat-e2e-other", cloneConfig.Settings[ProviderSettingKeys.Token]);
        Assert.Null(cloneConfig.Secrets);

        await agent.AcceptAndCompleteJobAsync(assignment.JobId);
    }
}
