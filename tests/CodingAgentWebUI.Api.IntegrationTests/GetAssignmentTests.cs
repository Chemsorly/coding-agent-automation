using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="WorkItemEndpoints.GetAssignment"/>.
/// Tests both the old-payload (full snapshot with ProviderConfigs != null)
/// and the new minimal-payload + fresh-fetch path (ProviderConfigs == null).
/// Also covers: <see cref="WorkItemEndpoints.BuildMinimalPayload"/> stripping mutable config.
/// </summary>
public sealed class GetAssignmentTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(string dbName)
    {
        var mock = new Mock<IDbContextFactory<PipelineDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var opts = new DbContextOptionsBuilder<PipelineDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                    .Options;
                return new TestableDbContext(opts);
            });
        return mock.Object;
    }

    private static IProjectStore CreateNullProjectStore()
    {
        var mock = new Mock<IProjectStore>();
        mock.Setup(ps => ps.GetProjectByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineProject?)null);
        return mock.Object;
    }

    private static async Task<Guid> SeedWorkItemAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemStatus status,
        string? payloadJson = null)
    {
        var id = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"test/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = status,
            Payload = payloadJson,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static JobDistributionRequest MakeFullRequest(string? steeringContent = null) => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet",
        TimeoutSeconds = 3600,
        // Old schema: ProviderConfigs != null
        ProviderConfigs =
        [
            new ProviderConfig
            {
                Id = "repo-1",
                Kind = ProviderKind.Repository,
                DisplayName = "Test Repo",
                ProviderType = "GitHub",
                SteeringContent = steeringContent
            }
        ],
        RepoSteeringContent = steeringContent,
        QualityGateConfigs = [],
        ReviewerConfigs = [],
        McpServers = [],
        PipelineConfiguration = new PipelineConfiguration(),
    };

    private static JobDistributionRequest MakeMinimalRequest() => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#42"),
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "dotnet",
        TimeoutSeconds = 3600,
        // New schema: PayloadSchemaVersion = 1 (set by BuildMinimalPayload)
        PayloadSchemaVersion = 1,
    };

    // ── Fake AssignmentEnricher ───────────────────────────────────────────────────

    /// <summary>
    /// Test stub for AssignmentEnricher. Uses the protected logger-only constructor so
    /// real dependencies (DispatchInfrastructure etc.) are not needed.
    /// Overrides the virtual EnrichAsync to control behavior in tests.
    /// </summary>
    private sealed class FakeAssignmentEnricher : AssignmentEnricher
    {
        private readonly Func<JobDistributionRequest, PipelineProject, Task<JobDistributionRequest?>> _enrich;
        public int CallCount { get; private set; }

        public FakeAssignmentEnricher(Func<JobDistributionRequest, PipelineProject, Task<JobDistributionRequest?>> enrich)
            : base(Serilog.Log.Logger) // logger-only protected ctor; real deps unused (EnrichAsync overridden)
        {
            _enrich = enrich;
        }

        public override async Task<JobDistributionRequest?> EnrichAsync(
            JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
        {
            CallCount++;
            return await _enrich(identity, project);
        }
    }

    /// <summary>
    /// Test stub that throws a configurable exception from EnrichAsync.
    /// Used to verify that transient enrichment failures cause GetAssignment to return 503.
    /// </summary>
    private sealed class ThrowingAssignmentEnricher : AssignmentEnricher
    {
        private readonly Exception _exception;
        public int CallCount { get; private set; }

        public ThrowingAssignmentEnricher(Exception exception)
            : base(Serilog.Log.Logger)
        {
            _exception = exception;
        }

        public override Task<JobDistributionRequest?> EnrichAsync(
            JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
        {
            CallCount++;
            throw _exception;
        }
    }

    // ── Old-payload path (full snapshot, ProviderConfigs != null) ──────────────

    [Fact]
    public async Task GetAssignment_OldPayload_ServedDirectlyFromSnapshot()
    {
        // ARRANGE: seed a work item with an old-schema payload (ProviderConfigs != null)
        var dbName = $"GetAssignment-OldPayload-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        const string frozenSteering = "frozen-steering-content-from-enqueue";
        var fullRequest = MakeFullRequest(frozenSteering);
        var payloadJson = JsonSerializer.Serialize(fullRequest, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        var enricher = new FakeAssignmentEnricher((r, p) => Task.FromResult<JobDistributionRequest?>(r));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: 200 returned, enricher NOT called (old schema served from snapshot)
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull("old-schema payload should return 200 OK");
        enricher.CallCount.Should().Be(0, "old-schema payload (ProviderConfigs != null) must not trigger enricher");

        // RepoSteeringContent from the frozen snapshot is returned
        okResult!.Value!.RepoSteeringContent.Should().Be(frozenSteering);
        // TODO: [WARNING] This test does not assert that the full frozen ProviderConfigs array is
        // forwarded intact in the response. A regression clearing ProviderConfigs on the old-schema
        // path before building JobAssignmentMessage would not be caught. Add:
        //   okResult.Value.ProviderConfigs.Should().NotBeEmpty("frozen snapshot must forward ProviderConfigs intact");
    }

    [Fact]
    public async Task GetAssignment_OldPayload_RunIdMatchesWorkItemId()
    {
        // ARRANGE
        var dbName = $"GetAssignment-RunId-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var payloadJson = JsonSerializer.Serialize(MakeFullRequest(), PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), null);

        // ASSERT: JobId in returned message matches the WorkItem GUID
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull();
        okResult!.Value!.JobId.Should().Be(id.ToString(), "RunId/JobId must match WorkItemEntity.Id for hub routing");
    }

    // ── New minimal-payload path (ProviderConfigs == null, fresh fetch) ─────────

    // TODO: [WARNING] No test covers the acceptance criterion "Tokens in ProviderConfigs are
    // freshly vended at assignment time (not expired snapshots)." There is no test that simulates
    // an old-schema work item with expired tokens and confirms the new-schema path returns freshly-
    // vended tokens, nor one that verifies the enricher receives the correct RepoProviderConfigId
    // and IssueProviderConfigId so token-vending is attempted for the right providers.

    [Fact]
    public async Task GetAssignment_MinimalPayload_CallsEnricherForFreshData()
    {
        // ARRANGE: seed with new-schema minimal payload (ProviderConfigs == null)
        var dbName = $"GetAssignment-MinimalPayload-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var minimalPayload = MakeMinimalRequest();
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        const string freshSteering = "fresh-steering-from-db";
        var enrichedRequest = minimalPayload with
        {
            ProviderConfigs =
            [
                new ProviderConfig
                {
                    Id = "repo-1",
                    Kind = ProviderKind.Repository,
                    DisplayName = "Test",
                    ProviderType = "GitHub"
                }
            ],
            RepoSteeringContent = freshSteering,
            QualityGateConfigs = [],
            ReviewerConfigs = [],
            McpServers = [],
            PipelineConfiguration = new PipelineConfiguration(),
        };

        var enricher = new FakeAssignmentEnricher((r, p) => Task.FromResult<JobDistributionRequest?>(enrichedRequest));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: 200 returned, enricher called once, fresh steering in response
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull("minimal-payload work item should return 200 OK after enrichment");
        enricher.CallCount.Should().Be(1, "new-schema payload (ProviderConfigs == null) must trigger enricher");
        okResult!.Value!.RepoSteeringContent.Should().Be(freshSteering,
            "RepoSteeringContent should reflect the freshly-fetched value from AssignmentEnricher");
    }

    [Fact]
    public async Task GetAssignment_MinimalPayload_RunIdMatchesWorkItemId()
    {
        // ARRANGE
        var dbName = $"GetAssignment-MinimalRunId-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var minimalPayload = MakeMinimalRequest();
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        // Enricher that returns enriched request
        var enrichedRequest = minimalPayload with
        {
            ProviderConfigs = [],
            QualityGateConfigs = [],
            ReviewerConfigs = [],
            McpServers = [],
            PipelineConfiguration = new PipelineConfiguration()
        };
        var enricher = new FakeAssignmentEnricher((r, p) => Task.FromResult<JobDistributionRequest?>(enrichedRequest));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: JobId matches WorkItem ID even after enrichment
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull();
        okResult!.Value!.JobId.Should().Be(id.ToString(),
            "JobId/RunId must always match WorkItemEntity.Id regardless of payload schema");
    }

    [Fact]
    public async Task GetAssignment_MinimalPayload_EnricherThrows_Returns503()
    {
        // ARRANGE: enricher throws a transient exception (DB timeout, provider failure, etc.)
        var dbName = $"GetAssignment-EnricherThrows-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var minimalPayload = MakeMinimalRequest();
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        // Enricher that throws a transient exception (simulates DB timeout, provider failure, etc.)
        var enricher = new ThrowingAssignmentEnricher(new InvalidOperationException("DB timeout"));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: 503 returned — agent must retry rather than proceed with an invalid job spec
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>()
            .Which.StatusCode.Should().Be(503,
                "enrichment failure must return 503 so the agent retries rather than using an identity-only payload");
        // TODO: [WARNING] CallCount on ThrowingAssignmentEnricher is never asserted here. Add:
        //   enricher.CallCount.Should().Be(1, "enricher must be invoked exactly once before the 503 is returned");
        // to confirm the 503 originates from the enricher call and not an earlier guard.
        // TODO: [WARNING] ProblemDetails content is not asserted. A regression that returned a bare 503 with
        // no body or leaked the internal exception message would not be caught. Add:
        //   ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).ProblemDetails.Detail
        //       .Should().Be("Assignment enrichment failed; please retry.");
    }

    [Fact]
    public async Task GetAssignment_MinimalPayload_EnricherReturnsNull_Returns503()
    {
        // ARRANGE: enricher returns null (no agent profile matched the selector — permanent config failure)
        var dbName = $"GetAssignment-EnricherNull-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var minimalPayload = MakeMinimalRequest();
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        // Enricher that returns null (simulates no profile matched — permanent failure)
        var enricher = new FakeAssignmentEnricher((r, p) => Task.FromResult<JobDistributionRequest?>(null));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: 503 returned — degraded identity-only 200 must not be served
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>()
            .Which.StatusCode.Should().Be(503,
                "null enrichment result (no profile matched) must return 503, not a degraded 200 with no configs");
        // TODO: [WARNING] The null-return and throw paths follow different internal code routes (null triggers
        // InvalidOperationException inside EnrichRequestAsync; throw propagates directly). Neither CallCount
        // nor ProblemDetails.Detail is asserted here. Add:
        //   ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).ProblemDetails.Detail
        //       .Should().Be("Assignment enrichment failed; please retry.");
        // to prevent regressions in the problem body format.
    }

    [Fact]
    public async Task GetAssignment_MinimalPayload_WithoutEnricher_ServedAsIdentityOnly()
    {
        // ARRANGE: no AssignmentEnricher registered (null)
        var dbName = $"GetAssignment-NoEnricher-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var minimalPayload = MakeMinimalRequest();
        var payloadJson = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        // ACT: no enricher → skip fresh-fetch path
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), assignmentEnricher: null);

        // ASSERT: 200 but without enriched data (ProviderConfigs will be empty defaults)
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull("should return 200 even without enricher (graceful degradation)");
        // TODO: [WARNING] No JobId assertion here. The minimum meaningful verification is that
        // the identity data is intact. Add: okResult!.Value!.JobId.Should().Be(id.ToString());
    }

    // ── Terminal status → 410 ────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task GetAssignment_TerminalStatus_Returns410(WorkItemStatus status)
    {
        var dbName = $"GetAssignment-Terminal-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var id = await SeedWorkItemAsync(dbFactory, status,
            JsonSerializer.Serialize(MakeFullRequest(), PipelineJsonOptions.Default));

        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), null);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task GetAssignment_NotFound_Returns404()
    {
        var dbName = $"GetAssignment-NotFound-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);

        var result = await WorkItemEndpoints.GetAssignment(
            Guid.NewGuid(), dbFactory, CreateNullProjectStore(), null);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    [Fact]
    public async Task GetAssignment_NullPayload_Returns404()
    {
        var dbName = $"GetAssignment-NullPayload-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson: null);

        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), null);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NotFound>();
    }

    // ── BuildMinimalPayload: confirm mutable config stripped ────────────────────

    [Fact]
    public void BuildMinimalPayload_StripsProviderConfigs()
    {
        var full = MakeFullRequest("some-steering");
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.ProviderConfigs.Should().BeNull("ProviderConfigs must be null in minimal payload");
    }

    [Fact]
    public void BuildMinimalPayload_StripsRepoSteeringContent()
    {
        var full = MakeFullRequest("some-steering");
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.RepoSteeringContent.Should().BeNull("RepoSteeringContent must be null in minimal payload");
    }

    [Fact]
    public void BuildMinimalPayload_StripsQualityGateConfigs()
    {
        var full = MakeFullRequest();
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.QualityGateConfigs.Should().BeNull("QualityGateConfigs must be null in minimal payload");
    }

    [Fact]
    public void BuildMinimalPayload_StripsMcpServers()
    {
        var full = MakeFullRequest();
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.McpServers.Should().BeNull("McpServers must be null in minimal payload");
    }

    [Fact]
    public void BuildMinimalPayload_StripsIssueComments()
    {
        var full = MakeFullRequest() with
        {
            IssueComments =
            [
                new IssueComment { Body = "a comment", Author = "bob", CreatedAt = DateTime.UtcNow, Id = "c1" }
            ]
        };
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.IssueComments.Should().BeNull("IssueComments must be null in minimal payload");
    }

    [Fact]
    public void BuildMinimalPayload_PreservesIdentityFields()
    {
        var runId = Guid.NewGuid().ToString();
        var full = MakeFullRequest() with
        {
            RunId = runId,
            BrainProviderConfigId = "brain-1",
            PipelineProviderConfigId = "pipeline-1",
            ProjectId = Guid.NewGuid(),
            ProjectName = "My Project",
            TraceContext = new Dictionary<string, string> { ["traceparent"] = "00-abc-def-01" }
        };
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);

        minimal.IssueIdentifier.Value.Should().Be(full.IssueIdentifier.Value);
        minimal.IssueProviderConfigId.Should().Be(full.IssueProviderConfigId);
        minimal.RepoProviderConfigId.Should().Be(full.RepoProviderConfigId);
        minimal.BrainProviderConfigId.Should().Be(full.BrainProviderConfigId);
        minimal.PipelineProviderConfigId.Should().Be(full.PipelineProviderConfigId);
        minimal.RunId.Should().Be(runId);
        minimal.AgentSelector.Should().Be(full.AgentSelector);
        minimal.ProjectId.Should().Be(full.ProjectId);
        minimal.ProjectName.Should().Be(full.ProjectName);
        minimal.InitiatedBy.Should().Be(full.InitiatedBy);
        minimal.TaskType.Should().Be(full.TaskType);
        minimal.RunType.Should().Be(full.RunType);
        minimal.TimeoutSeconds.Should().Be(full.TimeoutSeconds);
        minimal.TraceContext.Should().NotBeNull();
        // TODO: [WARNING] NotBeNull does not verify the trace context payload is actually preserved.
        // TraceContext is non-reconstructable (W3C spans are closed at assignment time). Add:
        //   minimal.TraceContext!["traceparent"].Should().Be("00-abc-def-01");
    }

    [Fact]
    public void BuildMinimalPayload_PreservesReviewSpecificIdentityFields()
    {
        var full = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#99"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600,
            LinkedPullRequest = new LinkedPullRequest
            {
                Url = "https://example.com/pr/99",
                BranchName = "fix/foo",
                IsDraft = false,
                Number = 99
            },
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = "Fix the bug",
            ReviewPrAuthor = "alice",
        };
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);

        minimal.LinkedPullRequest.Should().NotBeNull();
        minimal.LinkedPullRequest!.Url.Should().Be("https://example.com/pr/99");
        minimal.ReviewPrTargetBranch.Should().Be("main");
        minimal.ReviewPrDescription.Should().Be("Fix the bug");
        minimal.ReviewPrAuthor.Should().Be("alice");
        // TODO: [WARNING] No test verifies that consolidation identity fields are preserved:
        // ConsolidationRunType, ConsolidationTemplateId, ConsolidationWorkspacePath, AutoDispatch,
        // ProjectContext (decomposition), and LinkedIssueContexts (review). These are all listed
        // in the issue as non-reconstructable identity fields. Add a separate test method for them.
    }

    [Fact]
    public void BuildMinimalPayload_IssueDetailRetainsIdentityButStripsBody()
    {
        var full = MakeFullRequest() with
        {
            IssueDetail = new IssueDetail
            {
                Identifier = "owner/repo#42",
                Title = "Fix the crash",
                Description = "A very long issue body that should NOT be stored in the payload",
                Labels = ["bug", "priority:high"]
            }
        };
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);

        // Title kept for display in GetPendingWorkItems
        minimal.IssueDetail.Should().NotBeNull();
        minimal.IssueDetail!.Title.Should().Be("Fix the crash");
        // Body stripped to avoid storing stale content
        minimal.IssueDetail.Description.Should().Be(string.Empty,
            "IssueDetail.Description must be stripped from minimal payload");
        // TODO: [WARNING] Labels stripping is not asserted. BuildMinimalPayload sets Labels = [].
        // Add: minimal.IssueDetail.Labels.Should().BeEmpty("Labels must be stripped from minimal payload");
    }

    // ── BuildMinimalPayload: PayloadSchemaVersion ────────────────────────────────

    [Fact]
    public void BuildMinimalPayload_SetsPayloadSchemaVersion()
    {
        var full = MakeFullRequest("some-steering");
        var minimal = WorkItemEndpoints.BuildMinimalPayload(full);
        minimal.PayloadSchemaVersion.Should().Be(1,
            "BuildMinimalPayload must set PayloadSchemaVersion = 1 so GetAssignment can detect new-schema rows");
        // TODO: [WARNING] This test does not assert that PayloadSchemaVersion round-trips through JSON
        // serialization/deserialization. The discriminator only matters at read time (in GetAssignment),
        // so if PipelineJsonOptions.Default ever ignores or renames the field (e.g., via a
        // [JsonPropertyName] mismatch), this unit test would still pass while GetAssignment silently
        // falls back to the null-schema path. Add an explicit round-trip assertion:
        // var json = JsonSerializer.Serialize(minimal, PipelineJsonOptions.Default);
        // var deserialized = JsonSerializer.Deserialize<JobDistributionRequest>(json, PipelineJsonOptions.Default);
        // deserialized!.PayloadSchemaVersion.Should().Be(1, "PayloadSchemaVersion must survive JSON round-trip");
    }

    // ── Legacy null-schema path: PayloadSchemaVersion = null served as snapshot ──

    [Fact]
    public async Task GetAssignment_LegacyNullSchemaVersion_ServedFromSnapshot()
    {
        // ARRANGE: a row written after #2171 but before #2221 — ProviderConfigs is null
        // (identity-only content) but PayloadSchemaVersion was never set (null).
        // Under the new discriminator (PayloadSchemaVersion == 1), this must route to the
        // old-schema path (no enrichment) rather than entering the enrichment path.
        var dbName = $"GetAssignment-LegacyNull-{Guid.NewGuid():N}";
        var dbFactory = CreateDbFactory(dbName);

        // Build a minimal-style payload (no ProviderConfigs) but leave PayloadSchemaVersion = null
        var legacyRequest = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#42"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600,
            // PayloadSchemaVersion intentionally absent (null) — simulates pre-#2221 row
            // ProviderConfigs intentionally absent (null) — simulates minimal-style content
        };
        var payloadJson = JsonSerializer.Serialize(legacyRequest, PipelineJsonOptions.Default);
        var id = await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, payloadJson);

        var enricher = new FakeAssignmentEnricher((r, p) => Task.FromResult<JobDistributionRequest?>(r));

        // ACT
        var result = await WorkItemEndpoints.GetAssignment(
            id, dbFactory, CreateNullProjectStore(), enricher);

        // ASSERT: 200 returned, enricher NOT called
        // PayloadSchemaVersion == null (not 1) must route to the old-schema path even though
        // ProviderConfigs is also null — the versioned discriminator is immune to ProviderConfigs nullability.
        var okResult = result as Microsoft.AspNetCore.Http.HttpResults.Ok<JobAssignmentMessage>;
        okResult.Should().NotBeNull("legacy null-PayloadSchemaVersion row should return 200 OK");
        enricher.CallCount.Should().Be(0,
            "PayloadSchemaVersion == null must not trigger enricher — null means old-schema (snapshot) path");
        // TODO: [WARNING] This test only asserts routing (enricher not called) but does not verify that
        // the returned JobAssignmentMessage reflects the seeded payload. A regression where GetAssignment
        // returns 200 with a zeroed-out or incorrectly reconstructed message (e.g., because payload
        // deserialization silently failed before this point) would pass here since CallCount == 0 is
        // satisfied by any early return including error paths. Add a content assertion, e.g.:
        // okResult!.Value!.JobId.Should().Be(id.ToString(), "response must reflect the seeded work item");
    }

    // ── Infrastructure: PipelineDbContext in-memory subclass ──────────────────────

    private sealed class TestableDbContext : PipelineDbContext
    {
        public TestableDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }
}
