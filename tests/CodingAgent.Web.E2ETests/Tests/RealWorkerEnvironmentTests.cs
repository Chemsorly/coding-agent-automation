using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// What the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> does with the configuration of its assignment before
/// the agent runs: it masks project secrets in the output it reports, writes the MCP servers' config file, and runs the
/// repository's setup steps in the workspace. The worker runs in-process against the E2E API host, with the fake providers
/// injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// A <c>FakeAgentClient</c> test cannot cover this: it never runs <c>PipelineStepContext</c>, <c>WriteMcpConfigStep</c> or
/// <c>RunEnvironmentSetupStep</c>.
/// </para>
///
/// <para>
/// The configuration comes from the assignment, which the API builds when the worker claims the item. The tests therefore
/// seed it before the worker starts: project secrets need the project on the payload (<c>EnrichRequestAsync</c> looks the
/// project up by the payload's <c>ProjectId</c>), MCP servers come from the routed profile, and setup steps come from the
/// pipeline job template that owns the repository, not from the repository connection
/// (<c>RepositorySettings.WithTemplateSettings</c> copies them from the template and drops the connection's value).
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerEnvironmentTests : HeadlessE2ETestBase
{
    private const string SecretValue = "s3cr3t-e2e-value";
    private const string SecretLine = "token=" + SecretValue;
    private const string MaskedLine = "token=***";

    /// <summary>A project that is not the Default one, so that its secrets reach the assignment only through the payload.</summary>
    private static readonly Guid SecretsProjectId = Guid.Parse("22222222-3333-4444-5555-666666666666");

    public RealWorkerEnvironmentTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would.
    /// Returns the claimed work item GUID.
    ///
    /// <para>
    /// The real agent authenticates via <c>DeriveKey(masterApiKey, agentId)</c>. The API
    /// authorises <c>GET /api/work-items/{id}/assignment</c> by checking that the bearer token
    /// matches <c>DeriveKey(masterApiKey, AssignedAgentId)</c>. Pre-claiming the work item with
    /// <paramref name="agentId"/> as <c>AssignedAgentId</c> satisfies that check.
    /// </para>
    ///
    /// <para>
    /// A copy of <c>RealAgentWorkerSmokeTests.CreateAndClaimWorkItemAsync</c> with two additions.
    /// <paramref name="projectId"/> goes on the row and on the payload: the enricher reads the project, and so its secrets
    /// and MCP servers, from the payload, and the run takes its project from the row.
    /// <paramref name="mcpServers"/> go on the routed profile, which the enricher merges with the project's.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        Guid? projectId = null,
        IReadOnlyList<McpServerConfig>? mcpServers = null,
        CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e", projectId: projectId);

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        // Build the identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment
        // enriches it with configs. LocalPipelineExecutor's ProviderFactoryOverride still bypasses the
        // provider config lookup because fakeProviders is injected.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-smoke-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Implementation,
            ProjectId = projectId,
            IssueDetail = Fixture.IssueProvider.Issues.FirstOrDefault(i => i.Identifier == issueId)
                ?? new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the route pinned below, so the profile carries the MCP servers.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-agent",
            DisplayName = "Real agent smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
            McpServers = mcpServers ?? [],
        });
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(
            minimalPayload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        await db.SaveChangesAsync(ct);

        // Spec 056: POST /dispatch routes the item and pins its route; the claim refuses an item dispatched without one.
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, ct);
        Assert.Equal(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed, route.Status);

        // Marked Dispatched to the real agent, as the dispatch path would.
        await using var dispatchDb = Fixture.DbContextFactory.CreateDbContext();
        var dispatched = await dispatchDb.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
        dispatched.Status = WorkItemStatus.Dispatched;
        dispatched.AssignedAgentId = agentId;
        dispatched.DispatchedAt = DateTimeOffset.UtcNow;
        await dispatchDb.SaveChangesAsync(ct);

        return workItemId;
    }

    /// <summary>
    /// Seeds an <c>agent:next</c> issue and the one enabled template that owns <c>repo-e2e</c>, in
    /// <paramref name="projectId"/>. <paramref name="setupSteps"/> go on the template, which is where the worker's
    /// repository config gets them from.
    /// </summary>
    private async Task SeedIssueAndTemplateAsync(
        string issueId, string templateId, string projectId = WellKnownIds.DefaultProjectId,
        IReadOnlyList<SetupStep>? setupSteps = null)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Goal\nDo something.",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(projectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = templateId,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            SetupSteps = setupSteps
        }, CancellationToken.None);
    }

    /// <summary>
    /// Claims the item for a fresh agent id and runs the real worker to completion. The worker awaits its HTTP and hub
    /// reports before it stops, so on return the label swap and the work item's status are in place.
    /// </summary>
    private async Task<Guid> RunWorkerToCompletionAsync(string issueId, IReadOnlyList<McpServerConfig>? mcpServers = null)
    {
        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId, mcpServers: mcpServers);

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        return workItemId;
    }

    /// <summary>A snapshot of the output lines the API's run holds for the work item; empty until the run exists.</summary>
    private string[] OutputLinesOf(Guid workItemId) =>
        Fixture.RunService.GetRun(new RunId(workItemId.ToString()))?.OutputLines.ToArray() ?? [];

    /// <summary>
    /// The labels of <paramref name="issueId"/> that start with <c>agent:</c>. The API rewrites the fake's issue list from
    /// another thread, so a read that overlaps a write throws "Collection was modified"; the write is a single indexer set,
    /// so a plain re-read succeeds.
    /// </summary>
    private async Task<List<string>> ReadAgentLabelsAsync(string issueId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
                return issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();
            }
            catch (InvalidOperationException) when (attempt < 5)
            {
                // Read again.
            }
        }
    }

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    /// <summary>
    /// Wraps another script and reports the workspace path to <c>capture</c> just before the inner script runs.
    /// <c>AnalysisScript</c> is sealed, so it cannot be subclassed to do this.
    /// </summary>
    private sealed class CaptureScript(Action<string> capture, AgentScript inner) : AgentScript
    {
        public override void Execute(string workspacePath) { capture(workspacePath); inner.Execute(workspacePath); }
    }

    /// <summary>Stands in for the review call: the next agent call, code generation, waits on <paramref name="pause"/>.</summary>
    private sealed class SetPauseScript(ScriptedAgentProvider provider, TaskCompletionSource pause) : AgentScript
    {
        public override void Execute(string workspacePath) => provider.PauseBeforeExecution = pause;
    }

    // ── Secret masking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis agent prints a line that carries a project secret. Neither the output the API holds for the live run
    /// nor the output tail it saves in the history row may contain the secret: the worker replaces it with <c>***</c>
    /// before the line leaves the worker.
    ///
    /// <para>
    /// The run is held in code generation while the line is checked: the worker sends its output in batches every 250 ms,
    /// and the batch that is still open when the run ends is sent after the API has closed the run, so it is lost. The pause
    /// keeps the run alive until the masked line has landed, and the release lets it finish so the history row exists.
    /// The tail holds the last 200 lines and this run prints far fewer, so the line is still in it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenAgentOutputContainsProjectSecret_WorkerShouldMaskItBeforeItLeaves()
    {
        const string issueId = "6501";

        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RealAgentWorkerHarness();
        try
        {
            // ── Arrange ────────────────────────────────────────────────────────────────

            // The payload's project decides whether the worker gets the secret, so the project is saved before the claim.
            await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
            {
                Id = SecretsProjectId.ToString(),
                Name = "Secrets Project",
                Enabled = true,
                TemplateIds = [],
                Secrets = new Dictionary<string, string> { ["E2E_TOKEN"] = SecretValue }
            }, CancellationToken.None);
            await SeedIssueAndTemplateAsync(issueId, "real-env-secrets-template", SecretsProjectId.ToString());

            // Analysis (prints the secret), then the adversarial review call, which sets the pause: the next call waits.
            Fixture.AgentProvider
                .Enqueue(new AnalysisScript { OutputLines = [SecretLine] })
                .Enqueue(new SetPauseScript(Fixture.AgentProvider, pause));

            // ── Act ────────────────────────────────────────────────────────────────────

            var agentId = $"real-worker-{Guid.NewGuid():N}";
            var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId, SecretsProjectId);

            await harness.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await WaitUntilAsync(() => OutputLinesOf(workItemId).Contains(MaskedLine));
            var liveLines = OutputLinesOf(workItemId);

            pause.TrySetResult();
            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            // ── Assert ─────────────────────────────────────────────────────────────────

            liveLines.Should().NotContain(l => l.Contains(SecretValue, StringComparison.Ordinal),
                "the worker masks the secret before it reports the line");

            var row = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId);
            row.OutputTail.Should().NotBeNull().And.Contain(MaskedLine);
            row.OutputTail!.Should().NotContain(l => l.Contains(SecretValue, StringComparison.Ordinal),
                "the saved output tail is the masked output");
        }
        finally
        {
            pause.TrySetResult();
            await harness.DisposeAsync();
        }
    }

    // ── MCP servers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The routed profile names an MCP server. <c>WriteMcpConfigStep</c> writes the config file before the first agent call,
    /// so the analysis agent finds it; the run's completion payload names the server.
    ///
    /// <para>
    /// The config path is shared by every test in the process and is not removed after a run, so the test deletes it first
    /// (the content read in the agent call is then this run's) and again afterwards, and asserts nothing on it later.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenAssignmentHasMcpServers_ConfigShouldExistBeforeAgentRuns()
    {
        const string issueId = "6502";

        var configPath = Fixture.AgentProvider.McpConfigPath;
        try
        {
            if (File.Exists(configPath))
                File.Delete(configPath);

            await SeedIssueAndTemplateAsync(issueId, "real-env-mcp-template");

            string? configText = null;
            Fixture.AgentProvider
                .Enqueue(new CaptureScript(_ => configText = File.ReadAllText(configPath), new AnalysisScript()))
                .EnqueueReadyAnalysis()
                .EnqueueCodeGen();

            var workItemId = await RunWorkerToCompletionAsync(
                issueId, [new McpServerConfig { Name = "e2e-mcp", Command = "echo" }]);

            configText.Should().Contain("e2e-mcp", "the config file is written before the analysis agent runs");

            var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);
            ReadPayload(entity.Result).McpServers.Should().Contain("e2e-mcp");
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    // ── Setup steps ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The repository has a setup step that writes a marker file in the workspace. The marker exists when the analysis
    /// agent runs, so the step ran before the agent. The agent call reads it: the worker deletes the workspace when the run
    /// ends.
    ///
    /// <para>
    /// The command has no space before <c>&gt;</c> on purpose, so that it means the same in <c>cmd.exe</c> and in bash.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenRepositoryHasSetupStep_ItShouldRunBeforeAgent()
    {
        const string issueId = "6503";

        await SeedIssueAndTemplateAsync(issueId, "real-env-setup-template", setupSteps:
            [new SetupStep { Name = "write-marker", Command = "echo setup-ran> setup-marker.txt" }]);

        var markerExisted = false;
        Fixture.AgentProvider
            .Enqueue(new CaptureScript(
                workspacePath => markerExisted = File.Exists(Path.Combine(workspacePath, "setup-marker.txt")),
                new AnalysisScript()))
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        markerExisted.Should().BeTrue("the setup step runs in the workspace before the first agent call");
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);
    }

    /// <summary>
    /// A setup step exits with code 3. The run fails with the step's message and the issue ends <c>agent:error</c>, no agent
    /// call is made, and no pull request is created.
    ///
    /// <para>
    /// <c>FailureReason</c> is not asserted: <c>FailRunAsync</c> sets no failure category, so the persisted value is the
    /// resolver's default. The run's output lines are not asserted either: a run that ends this fast loses the lines of its
    /// last batch (see <see cref="WhenAgentOutputContainsProjectSecret_WorkerShouldMaskItBeforeItLeaves"/>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenSetupStepFails_RunShouldFailBeforeAgent()
    {
        const string issueId = "6504";

        await SeedIssueAndTemplateAsync(issueId, "real-env-failing-setup-template", setupSteps:
            [new SetupStep { Name = "failing-step", Command = "exit 3" }]);

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        entity.ErrorMessage.Should().StartWith("Setup step 'failing-step' failed with exit code 3:");
        (await ReadAgentLabelsAsync(issueId)).Should().Equal(AgentLabels.Error);
        Fixture.AgentProvider.Calls.Should().BeEmpty("the run fails before the first agent call");
        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a run that fails in setup leaves no pull request");
    }
}
