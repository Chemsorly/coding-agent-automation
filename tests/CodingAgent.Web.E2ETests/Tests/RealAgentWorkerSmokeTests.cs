using CodingAgent.Agent;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke tests that run the real <see cref="WorkItemAgentService"/> in-process against the
/// E2E API host.  Each test builds an isolated <see cref="IHost"/> that wires the real
/// WorkItemAgentService, AgentConnectionManager, HubConnectionManager and
/// HttpPrimaryCompletionReporter against the live Kestrel / MessagePack / AgentHub stack,
/// while substituting <see cref="ScriptedAgentProvider"/> / <see cref="InMemoryRepositoryProvider"/>
/// for the LLM and git operations.
///
/// Acceptance criteria:
/// - Scenario A (implementation happy path) ends with WorkItem = Succeeded, PR created, label
///   swapped to agent:done and an analysis comment posted through the hub.
/// - Scenario B (decomposition Phase 1) ends with the plan comment posted and label swapped to
///   agent:epic-review.  A second run updates the same comment rather than posting a new one,
///   proving that the IssueIdentifierFormatter wire contract is covered.
///
/// IssueIdentifierFormatter wire-contract falsifiability (Scenario B):
///   The real falsifiability anchor is
///   OrchestratorProxyWireContractTests.IssueIdentifierFormatter_SerializesAsBareString_AsRequiredByHubContract,
///   which directly asserts the bare-string wire format.
///
///   In Scenario B's runtime structure, reverting IssueIdentifierFormatter from
///   AgentHubMessagePack.SerializerOptions causes the FIRST hub call in
///   PostDecompositionPlanStep (RequestListComments) to fail binding — not the update call.
///   PostDecompositionPlanStep.ExecuteAsync wraps both ListCommentsAsync and UpdateCommentAsync
///   inside context.TryCriticalAsync.  Without the formatter, RequestListComments (hub signature:
///   string identifier) fails to bind because MessagePack serialises IssueIdentifier as a map
///   rather than a bare string.  SignalR returns a completion error, the resilience pipeline
///   rethrows, and TryCriticalAsync calls FailRunAsync and returns StepResult.Stop.  The run
///   ends Failed, never reaching PipelineStep.Completed, so WaitForWorkItemStatusAsync(Succeeded)
///   times out.  The test therefore fails on the status wait — not via the PostedComments count.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealAgentWorkerSmokeTests : HeadlessE2ETestBase
{
    public RealAgentWorkerSmokeTests(E2EFixture fixture) : base(fixture) { }

    // ── shared setup helpers ────────────────────────────────────────────

    /// <summary>
    /// Seeds a template and an agent profile that <see cref="HeadlessE2ETestBase.DispatchIssueAsync"/>
    /// needs to resolve a <see cref="JobDistributionRequest"/>.
    /// </summary>
    // TODO [WARNING]: The template seeded here is not actually consulted during dispatch because
    // DispatchAndBuildAgentHostAsync calls DispatchIssueAsync(issueIdentifier, ct: ct) with no
    // templateId. HeadlessE2ETestBase.DispatchIssueAsync falls back to hardcoded provider IDs
    // ("issue-e2e" / "repo-e2e") when templateId is null, so the seeded template's
    // DecompositionEnabled / ImplementationEnabled flags are never applied via the template
    // resolution path. Dispatch routing is currently correct only because the default provider IDs
    // happen to match and PrepareDistributionRequestAsync routes by label (agent:next /
    // agent:epic) rather than by template flags. If that coincidence breaks, both scenarios would
    // silently dispatch with the wrong configuration. Fix: pass templateId: "template-real-agent"
    // to DispatchIssueAsync (or propagate it through DispatchAndBuildAgentHostAsync) so the
    // template is actually selected.
    private async Task SeedProfileAndTemplateAsync(bool decompositionEnabled = false)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(
            WellKnownIds.DefaultProjectId,
            new PipelineJobTemplate
            {
                Id = "template-real-agent",
                Name = "Real Agent Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true,
                DecompositionEnabled = decompositionEnabled,
                ImplementationEnabled = !decompositionEnabled,
            },
            CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(
            new AgentProfile
            {
                Id = "profile-real-agent",
                DisplayName = "Real Agent Profile",
                MatchLabels = [],  // empty selector matches any idle agent
                AgentProviderConfigId = "agent-e2e",
                Enabled = true,
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Builds an isolated <see cref="IHost"/> that runs a single
    /// <see cref="WorkItemAgentService"/> instance against the live E2E API host.
    ///
    /// <para>
    /// Prerequisites (must be met BEFORE calling this method):
    /// - The work item must have been created in the DB (<paramref name="workItemId"/> known).
    /// - <see cref="FakeJobController.DispatchOnceAsync"/> must have claimed it so
    ///   AssignedAgentId is written and the GET /assignment endpoint will authorise the agent.
    /// </para>
    /// </summary>
    private IHost BuildAgentHost(AgentId agentId, string derivedKey, Guid workItemId)
    {
        var startupConfig = new AgentStartupConfig
        {
            AgentApiKey = derivedKey,
            OrchestratorUrl = Fixture.AgentHubUrl,
            AgentId = agentId,
            WorkItemId = workItemId.ToString(),
            IsWorkItemMode = true,
            KeyIsPreDerived = true,
        };

        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // ── Serilog ──
                services.AddSingleton(Serilog.Log.Logger);

                // ── KiroCliLib — required for LocalPipelineExecutor construction even though
                //    ProviderFactoryOverride bypasses it at execution time ──
                var kiroConfig = new Configuration
                {
                    KiroCliPath = "/nonexistent-kiro",
                    WorkspaceDirectory = Path.GetTempPath(),
                };
                services.AddSingleton(kiroConfig);
                services.AddSingleton<IKiroCliOrchestrator>(sp =>
                    new KiroCliOrchestrator(sp.GetRequiredService<Configuration>(), Serilog.Log.Logger));

                // ── Pipeline config and history service ──
                services.AddSingleton(new PipelineConfiguration());
                services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

                // ── Shared pipeline services.  AddPipelineServices requires IMeterFactory which
                //    Host.CreateDefaultBuilder() provides automatically. ──
                services.AddPipelineServices(Serilog.Log.Logger);

                // Replace the real QualityGateValidator registered by AddPipelineServices with the
                // fixture's ConfigurableQualityGateValidator (pre-configured to always pass).
                services.RemoveAll<IQualityGateValidator>();
                services.AddSingleton<IQualityGateValidator>(Fixture.QualityGateValidator);

                // ── Brain update service — no-op to avoid real git operations ──
                services.AddSingleton<IBrainUpdateService>(Mock.Of<IBrainUpdateService>());

                // ── Agent identity — struct, requires non-generic ServiceDescriptor ──
                services.Add(ServiceDescriptor.Singleton(typeof(AgentId), agentId));

                // ── Hub connection manager ──
                services.AddSingleton<IHubConnectionManagerFactory>(sp =>
                    new HubConnectionManagerFactory(
                        Fixture.AgentHubUrl,
                        agentId,
                        derivedKey,
                        Serilog.Log.Logger,
                        keyIsPreDerived: true));
                services.AddSingleton<IHubConnectionManager>(sp =>
                    sp.GetRequiredService<IHubConnectionManagerFactory>().Create());

                // ── Reporter factory ──
                services.AddSingleton<IPipelineReporterFactory>(sp =>
                    new PipelineReporterFactory(Serilog.Log.Logger));

                // ── Pipeline executor — injects FakeProviders so ScriptedAgentProvider and
                //    InMemoryRepositoryProvider are used instead of the real implementations ──
                services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
                    new LocalPipelineExecutorDependencies(
                        sp.GetRequiredService<IKiroCliOrchestrator>(),
                        sp.GetRequiredService<IHttpClientFactory>(),
                        sp.GetRequiredService<PipelineConfiguration>(),
                        sp.GetRequiredService<IQualityGateValidator>(),
                        Serilog.Log.Logger,
                        sp.GetRequiredService<IBrainUpdateService>(),
                        AgentIdentity: agentId,
                        ReporterFactory: sp.GetRequiredService<IPipelineReporterFactory>(),
                        ProviderFactoryOverride: Fixture.FakeProviders)));

                // ── Consolidation executor ──
                services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
                    sp.GetRequiredService<IKiroCliOrchestrator>(),
                    sp.GetRequiredService<IHttpClientFactory>(),
                    Serilog.Log.Logger));

                // ── WorkItem-mode registrations: WorkItemHttpClient, WorkItemExecutorRouter,
                //    AgentConnectionManager, HttpPrimaryCompletionReporter, WorkItemAgentService.
                //    IHostApplicationLifetime from this isolated host is used by AgentConnectionManager,
                //    so StopApplication() stops this host only — not the test process. ──
                services.AddK8sModeServices(startupConfig, Serilog.Log.Logger);
            })
            .Build();
    }

    /// <summary>
    /// Dispatches an issue, claims the resulting work item synchronously via
    /// <see cref="FakeJobController.DispatchOnceAsync"/>, then builds the isolated agent host.
    /// Returns the work item ID and the started host.
    /// </summary>
    private async Task<(Guid workItemId, IHost host)> DispatchAndBuildAgentHostAsync(
        string issueIdentifier,
        CancellationToken ct = default)
    {
        // 1. Dispatch the issue — creates the work item in Pending state
        var result = await DispatchIssueAsync(issueIdentifier, ct: ct);
        Assert.True(result.Success, $"DispatchIssueAsync failed: {result.ErrorMessage}");
        Assert.NotNull(result.WorkItemId);
        var workItemId = Guid.Parse(result.WorkItemId!);

        // 2. Generate agent identity before the claim so AssignedAgentId matches the host's key
        var agentId = new AgentId($"e2e-real-{workItemId:N}");
        var derivedKey = HubConnectionManager.DeriveKey(Fixture.ApiKey, agentId.Value);

        // 3. Explicitly claim the work item so AssignedAgentId is written before the agent host
        //    calls GET /api/work-items/{id}/assignment.  FakeJobController.TryGetConnected
        //    returns false for our agentId (no FakeAgentClient registered), so
        //    StartAssignedWorkItemAsync is skipped — expected and correct for the real worker.
        //    We poll DispatchOnceAsync until the item is claimed so the test is not timing-sensitive.
        // TODO [WARNING]: The poll loop exits as soon as AssignedAgentId is non-empty but does not
        // confirm Status == WorkItemStatus.Dispatched. If the status has already advanced beyond
        // Dispatched by the time the real agent host reads GET /api/work-items/{id}/assignment,
        // the endpoint may return null (terminal-already path), WorkItemAgentService logs
        // "already terminal" and exits with code 0 without reaching Succeeded. Add a check:
        //   if (entity is not null && !string.IsNullOrEmpty(entity.AssignedAgentId)
        //       && entity.Status == WorkItemStatus.Dispatched) break;
        // TODO [WARNING]: DispatchOnceAsync is invoked both from this claim loop and concurrently
        // from FakeJobController's own 250 ms PollAsync loop. DispatchOnceAsync is not documented
        // as re-entry-safe against a concurrent second caller; it mutates _inFlight and issues
        // ClaimAsync calls. Concurrent execution relies on the API's claim endpoint returning 409
        // for the loser and on _inFlight.TryAdd. This is probably safe, but combined with the
        // AssignedAgentId lost-update race below it compounds flake risk under CI load.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Fixture.JobController.DispatchOnceAsync(ct);
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var entity = await db.WorkItems.FindAsync([workItemId], ct);
            if (entity is not null && !string.IsNullOrEmpty(entity.AssignedAgentId))
                break;
            await Task.Delay(50, ct);
        }

        // Verify the claim succeeded before building the host
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var entity = await db.WorkItems.FindAsync([workItemId], ct);
            Assert.NotNull(entity?.AssignedAgentId);

            // TODO [WARNING]: AssignedAgentId patch races the FakeJobController poll loop (250 ms
            // tick). If the controller claims the item again between the verification read above
            // and SaveChangesAsync below, the patched AssignedAgentId is overwritten, the API
            // rejects the real agent's key, and the test times out in WaitForWorkItemStatusAsync.
            // Fix: call FakeJobController.ForgetInFlight(workItemId) after the patch, or drive
            // the claim with a mechanism that sets exactly the chosen agentId from the start.
            // Patch AssignedAgentId to match our chosen agentId so the API authorises the key
            entity!.AssignedAgentId = agentId.Value;
            await db.SaveChangesAsync(ct);
        }

        // 4. Build the isolated agent host with the known workItemId
        var host = BuildAgentHost(agentId, derivedKey, workItemId);

        return (workItemId, host);
    }

    // ── Scenario A: Implementation happy path ──────────────────────────

    [Fact]
    public async Task ScenarioA_ImplementationHappyPath_WorkItemSucceeds_PrCreated_LabelSwapped_CommentPosted()
    {
        // ── Arrange ──────────────────────────────────────────────────────

        // Seed an issue with agent:next so DispatchOrchestrationService routes it as Implementation
        const string issueId = "3086-a";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Implement feature X",
            Description = "## Requirements\nDo the thing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:next"],
            Url = $"https://github.com/test/repo/issues/{issueId}",
        });
        await SeedProfileAndTemplateAsync();

        // Queue analysis (ready) + code generation on the scripted provider
        Fixture.AgentProvider
            .EnqueueReadyAnalysis("Analysis complete: everything looks good.")
            .EnqueueCodeGen();

        var (workItemId, host) = await DispatchAndBuildAgentHostAsync(issueId);

        // ── Act ──────────────────────────────────────────────────────────
        await host.StartAsync();
        try
        {
            var workItem = await WaitForWorkItemStatusAsync(
                workItemId, WorkItemStatus.Succeeded,
                timeout: TimeSpan.FromSeconds(20));

            // ── Assert ───────────────────────────────────────────────────
            // Work item reached Succeeded
            Assert.Equal(WorkItemStatus.Succeeded, workItem.Status);

            // A PR was created through InMemoryRepositoryProvider
            Assert.NotNull(Fixture.RepositoryProvider.LastCreatedPrUrl);

            // Issue label was swapped to agent:done via the real OrchestratorProxy → AgentHub
            var issue = Fixture.IssueProvider.Issues.Single(i => i.Identifier == issueId);
            Assert.Contains("agent:done", issue.Labels);
            Assert.DoesNotContain("agent:next", issue.Labels);

            // Analysis comment was posted through the hub (OrchestratorProxy → RequestPostComment
            // → AgentHub → InMemoryIssueProvider.PostCommentAsync)
            Assert.NotEmpty(Fixture.IssueProvider.PostedComments);
        }
        finally
        {
            await host.StopAsync();
            // TODO [WARNING]: host.Dispose() is synchronous; IHubConnectionManager implements
            // IAsyncDisposable and HubConnection.DisposeAsync() is async. The DI container's
            // synchronous Dispose() schedules DisposeAsync() fire-and-forget, leaving the
            // underlying SignalR HubConnection potentially open when the next test begins.
            // Fix: use await host.DisposeAsync() so the connection is fully closed before
            // the test completes. Applies to the same pattern in Scenario B (host1, host2).
            host.Dispose();
        }
    }

    // ── Scenario B: Decomposition Phase 1 + IssueIdentifierFormatter wire contract ──

    [Fact]
    public async Task ScenarioB_DecompositionPhase1_PlanPosted_LabelSwapped_SecondRunUpdatesComment()
    {
        // ── Arrange ──────────────────────────────────────────────────────

        const string issueId = "3086-b";
        const string decompositionPlanMarker = "<!-- agent:decomposition-plan -->";

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Epic: Build the whole thing",
            Description = "## Goal\nBig epic\n\n## Sub-tasks\n- Task 1\n- Task 2",
            Labels = ["agent:epic"],
            Url = $"https://github.com/test/repo/issues/{issueId}",
        });
        await SeedProfileAndTemplateAsync(decompositionEnabled: true);

        // Queue Phase 1 analysis script — the content must include the plan marker so the
        // pipeline embeds it in the plan comment body
        var planContent = $"{decompositionPlanMarker}\n\n## Plan\n- Step 1\n- Step 2\n" +
                          new string('x', 200); // length requirement for AnalysisScript
        Fixture.AgentProvider.EnqueueReadyAnalysis(planContent);

        // ── Act: Run 1 ───────────────────────────────────────────────────
        var (workItemId1, host1) = await DispatchAndBuildAgentHostAsync(issueId);
        await host1.StartAsync();
        try
        {
            await WaitForWorkItemStatusAsync(
                workItemId1, WorkItemStatus.Succeeded,
                timeout: TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host1.StopAsync();
            // TODO [WARNING]: Use await host1.DisposeAsync() instead of host1.Dispose() to ensure
            // the IHubConnectionManager's HubConnection is fully closed asynchronously before
            // proceeding. See the same note on Scenario A's finally block.
            host1.Dispose();
        }

        // ── Assert: Run 1 ────────────────────────────────────────────────

        // The plan comment was posted
        Assert.Single(Fixture.IssueProvider.PostedComments);
        var (_, firstCommentBody) = Fixture.IssueProvider.PostedComments[0];
        Assert.Contains(decompositionPlanMarker, firstCommentBody);

        // Label was swapped to agent:epic-review
        var issue = Fixture.IssueProvider.Issues.Single(i => i.Identifier == issueId);
        Assert.Contains("agent:epic-review", issue.Labels);
        Assert.DoesNotContain("agent:epic", issue.Labels);

        // ── Arrange: Run 2 — second Phase 1 dispatch on the same issue ───

        // Simulate human editing the plan back to agent:epic for a second Phase 1 run
        var idx = Fixture.IssueProvider.Issues.FindIndex(i => i.Identifier == issueId);
        var currentIssue = Fixture.IssueProvider.Issues[idx];
        var updatedLabels = currentIssue.Labels
            .Where(l => l != "agent:epic-review")
            .Append("agent:epic")
            .ToList();
        Fixture.IssueProvider.Issues[idx] = new IssueDetail
        {
            Identifier = currentIssue.Identifier,
            Title = currentIssue.Title,
            Description = currentIssue.Description,
            Labels = updatedLabels,
            Url = currentIssue.Url,
            Images = currentIssue.Images,
        };

        var plan2Content = $"{decompositionPlanMarker}\n\n## Plan (revised)\n- Step A\n- Step B\n" +
                           new string('y', 200);
        Fixture.AgentProvider.EnqueueReadyAnalysis(plan2Content);

        // ── Act: Run 2 ───────────────────────────────────────────────────
        // TODO [WARNING]: DispatchAndBuildAgentHostAsync → DispatchIssueAsync →
        // PrepareDistributionRequestAsync may include a dedup guard that blocks re-dispatch on
        // an issue whose prior WorkItem row is in a terminal state, or that inspects LabelChanges
        // history rather than live labels only. If the guard fires here, result.Success == false
        // and Assert.True(result.Success) fails with a misleading "DispatchIssueAsync failed"
        // message rather than indicating the real cause (dedup). If Run 2 dispatch becomes flaky,
        // check whether the dedup guard needs to be relaxed for re-dispatch on completed epics.
        var (workItemId2, host2) = await DispatchAndBuildAgentHostAsync(issueId);
        await host2.StartAsync();
        try
        {
            await WaitForWorkItemStatusAsync(
                workItemId2, WorkItemStatus.Succeeded,
                timeout: TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host2.StopAsync();
            // TODO [WARNING]: Use await host2.DisposeAsync() instead of host2.Dispose() to ensure
            // the IHubConnectionManager's HubConnection is fully closed asynchronously before
            // proceeding. See the same note on Scenario A's finally block.
            host2.Dispose();
        }

        // ── Assert: Run 2 ────────────────────────────────────────────────

        // The EXISTING plan comment was UPDATED (not a new post) — this is the
        // IssueIdentifierFormatter wire-contract assertion.
        //
        // OrchestratorProxy.UpdateCommentAsync sends RequestUpdateComment with issueIdentifier
        // typed as IssueIdentifier (a struct).  AgentHubMessagePack.SerializerOptions includes
        // IssueIdentifierFormatter which serialises it as a bare string.
        //
        // Falsifiability: reverting IssueIdentifierFormatter does NOT cause PostedComments.Count
        // to reach 2 (that path is unreachable).  Instead, the failure occurs earlier: with the
        // formatter absent, RequestListComments (hub signature: string identifier) fails to bind
        // because MessagePack serialises IssueIdentifier as a map, not a bare string.
        // PostDecompositionPlanStep wraps both calls inside context.TryCriticalAsync; the binding
        // failure causes TryCriticalAsync to call FailRunAsync (StepResult.Stop).  The run ends
        // Failed, so WaitForWorkItemStatusAsync(..., Succeeded, 20s) times out and the test fails
        // before these asserts are evaluated.
        //
        // The direct wire-contract anchor is:
        //   OrchestratorProxyWireContractTests.IssueIdentifierFormatter_SerializesAsBareString_AsRequiredByHubContract
        Assert.Single(Fixture.IssueProvider.PostedComments);   // still only one post
        Assert.Single(Fixture.IssueProvider.UpdatedComments);  // the update happened

        var (_, _, updatedBody) = Fixture.IssueProvider.UpdatedComments[0];
        Assert.Contains(decompositionPlanMarker, updatedBody);
    }
}
