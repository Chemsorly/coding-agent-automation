using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Issue images on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API host,
/// with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// An image URL that points at the local network must not get a connection from the worker, and a refused image must not
/// stop the run. <c>SsrfGuard</c> has unit tests; nothing else checks that the worker's download step uses it.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerIssueImageTests : HeadlessE2ETestBase
{
    public RealWorkerIssueImageTests(E2EFixture fixture) : base(fixture) { }

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
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e");

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = taskType;

        // Build the identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment
        // enriches it with configs. LocalPipelineExecutor's ProviderFactoryOverride still bypasses the
        // provider config lookup because fakeProviders is injected.
        var runType = taskType == WorkItemTaskType.Decomposition
            ? PipelineRunType.DecompositionAnalysis
            : PipelineRunType.Implementation;
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-smoke-test",
            TaskType = taskType,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = runType,
            IssueDetail = Fixture.IssueProvider.Issues.FirstOrDefault(i => i.Identifier == issueId)
                ?? new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the "e2e" selector.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-agent",
            DisplayName = "Real agent smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
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
    /// Wraps another script and reports the workspace path to <c>capture</c> just before the inner script runs.
    /// <c>AnalysisScript</c> is sealed, so it cannot be subclassed to do this.
    /// </summary>
    private sealed class CaptureScript(Action<string> capture, AgentScript inner) : AgentScript
    {
        public override void Execute(string workspacePath) { capture(workspacePath); inner.Execute(workspacePath); }
    }

    // ── loopback image ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The issue body embeds an image on <c>127.0.0.1</c>, where a listener counts every connection it accepts. The worker
    /// must open none, no file lands in <c>.agent/images</c>, and the run still succeeds.
    ///
    /// <para>
    /// The listener closes each client it accepts. Without that the TLS handshake of a connection that got through would
    /// hang until the 60 s download timeout, and a regression would show as a timeout instead of a count.
    /// </para>
    ///
    /// <para>
    /// A count of zero also holds when the download never starts, so the test proves that it did. The download step asks
    /// the hub for a repository token first, and the seeded <c>repo-e2e</c> config has no credentials: without one the
    /// step logs a warning and moves on before it creates <c>.agent/images</c>. The config is saved here with an access
    /// token, and the analysis script, which runs after the download step, records whether the directory exists.
    /// </para>
    ///
    /// <para>
    /// The check on <c>.agent/images</c> reads it during the analysis call, because the worker deletes the whole workspace
    /// when the run ends.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenIssueImagePointsAtLoopback_WorkerShouldNotConnectAndRunShouldContinue()
    {
        const string issueId = "6603";

        var connections = 0;
        using var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Count first, then close: the worker only sees the failed handshake after the close, so the count is final by then.
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    Interlocked.Increment(ref connections);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        try
        {
            // ── Arrange ────────────────────────────────────────────────────────────────

            Fixture.IssueProvider.Issues.Add(new IssueDetail
            {
                IsAuthorVerified = true,
                Identifier = issueId,
                Title = "Fix the layout shown in the screenshot",
                Description = $"## Requirements\n![shot](https://127.0.0.1:{port}/shot.png)\n\n## Acceptance Criteria\n- [ ] Works",
                Labels = ["agent:next"]
            });

            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "real-image-template",
                Name = "Real Image Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);

            await Fixture.SeedRoutedProfileAsync(new AgentProfile
            {
                Id = "real-image-profile",
                DisplayName = "Real Image Profile",
                JobTemplate = E2ERouting.Key(["e2e"]),
            });

            // The seeded repo-e2e has no credentials, so the hub cannot vend the token the download step asks for.
            // ResetAll seeds the defaults again for the next test.
            await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
            {
                Id = "repo-e2e",
                Kind = ProviderKind.Repository,
                ProviderType = "GitHub",
                DisplayName = "E2E Repo Provider",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.AccessToken] = "e2e-image-token" }
            }, CancellationToken.None);

            string? workspacePath = null;
            var imagesDirExistedAtAnalysis = false;
            string[] imageFilesAtAnalysis = [];

            Fixture.AgentProvider
                .Enqueue(new CaptureScript(
                    ws =>
                    {
                        workspacePath = ws;
                        var imagesDirAtAnalysis = Path.Combine(ws, ".agent", "images");
                        imagesDirExistedAtAnalysis = Directory.Exists(imagesDirAtAnalysis);
                        imageFilesAtAnalysis = imagesDirExistedAtAnalysis ? Directory.GetFiles(imagesDirAtAnalysis) : [];
                    },
                    new AnalysisScript()))  // analysis agent
                .EnqueueReadyAnalysis()     // adversarial review agent
                .EnqueueCodeGen();          // code generation agent

            Fixture.QualityGateValidator.AlwaysPass();

            // ── Act ────────────────────────────────────────────────────────────────────

            var agentId = $"real-worker-{Guid.NewGuid():N}";
            var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

            await using var harness = new RealAgentWorkerHarness();
            await harness.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            // ── Assert ─────────────────────────────────────────────────────────────────

            await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

            Volatile.Read(ref connections).Should().Be(0,
                "SsrfGuard must refuse a loopback address before the worker opens a socket");

            workspacePath.Should().NotBeNull("the analysis script ran");
            imagesDirExistedAtAnalysis.Should().BeTrue(
                "the download step must have started, or a count of zero proves nothing");
            imageFilesAtAnalysis.Should().BeEmpty("a refused image leaves no file");

            // The workspace is gone once the run has ended, so this holds whatever the download did.
            var imagesDir = Path.Combine(workspacePath!, ".agent", "images");
            (Directory.Exists(imagesDir) ? Directory.GetFiles(imagesDir) : []).Should().BeEmpty();
        }
        finally
        {
            await cts.CancelAsync();
            listener.Stop();
            await acceptLoop;
        }
    }
}
