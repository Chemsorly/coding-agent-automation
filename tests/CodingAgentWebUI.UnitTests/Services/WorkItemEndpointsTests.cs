using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for Work Item HTTP API endpoints (GET /api/work-items/{id}/assignment, POST /api/work-items/{id}/status).
/// Validates: Requirements 6.1, 6.2, 6.5, 6.6, 1.14
/// </summary>
public class WorkItemEndpointsTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<IOrchestratorRunService> _runService;
    private readonly WorkItemTransitionService _transitionService;

    public WorkItemEndpointsTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"WorkItemEndpointsTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new Mock<IOrchestratorRunService>();
        _transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    private async Task SeedWorkItemAsync(Guid id, WorkItemStatus status, string? payload = null)
    {
        await using var db = _dbFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            Status = status,
            Payload = payload,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "ipc-1",
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    // ── GET /api/work-items/{id}/assignment ──────────────────────────────

    [Fact]
    public async Task GetAssignment_ReturnsNotFound_WhenWorkItemDoesNotExist()
    {
        var result = await WorkItemEndpoints.GetAssignment(Guid.NewGuid(), _dbFactory);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task GetAssignment_ReturnsGone_WhenWorkItemIsInTerminalStatus()
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(CreateTestPayload(), PipelineJsonOptions.Default);
        await SeedWorkItemAsync(id, WorkItemStatus.Succeeded, payload);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        result.Should().BeOfType<StatusCodeHttpResult>();
        ((StatusCodeHttpResult)result).StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task GetAssignment_Returns200_WithJobAssignmentDto_WhenWorkItemIsActive()
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(CreateTestPayload(), PipelineJsonOptions.Default);
        await SeedWorkItemAsync(id, WorkItemStatus.Dispatched, payload);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        result.Should().BeOfType<Ok<WorkItemAssignmentDto>>();
        var okResult = (Ok<WorkItemAssignmentDto>)result;
        okResult.Value.Should().NotBeNull();
        okResult.Value!.IssueIdentifier.Should().Be("owner/repo#1");
        okResult.Value.RepoProviderConfigId.Should().Be("repo-1");
        okResult.Value.JobId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetAssignment_ReturnsNotFound_WhenPayloadIsNull()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Dispatched, payload: null);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        result.Should().BeOfType<NotFound>();
    }

    // ── POST /api/work-items/{id}/status ─────────────────────────────────

    [Fact]
    public async Task PostStatus_ReturnsNotFound_WhenWorkItemDoesNotExist()
    {
        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running, AgentId = "agent-1" };

        var result = await WorkItemEndpoints.PostStatus(
            Guid.NewGuid(), request, _transitionService, _runService.Object, _dbFactory);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task PostStatus_Returns200_OnValidTransition()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Dispatched);

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running, AgentId = "agent-1" };

        var result = await WorkItemEndpoints.PostStatus(
            id, request, _transitionService, _runService.Object, _dbFactory);

        result.Should().BeOfType<Ok>();
    }

    [Fact]
    public async Task PostStatus_Returns400_OnInvalidTransition()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Succeeded);

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running, AgentId = "agent-1" };

        var result = await WorkItemEndpoints.PostStatus(
            id, request, _transitionService, _runService.Object, _dbFactory);

        result.Should().BeOfType<BadRequest<string>>();
    }

    [Fact]
    public async Task PostStatus_SetsAgentIdOnTransitionToRunning()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Dispatched);

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Running, AgentId = "agent-42" };

        await WorkItemEndpoints.PostStatus(id, request, _transitionService, _runService.Object, _dbFactory);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var item = await verifyDb.WorkItems.FindAsync(id);
        item!.AssignedAgentId.Should().Be("agent-42");
    }

    [Fact]
    public async Task PostStatus_SetsCompletedAtAndErrorOnFailure()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Running);

        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Failed,
            AgentId = "agent-1",
            ErrorMessage = "Something broke"
        };

        await WorkItemEndpoints.PostStatus(id, request, _transitionService, _runService.Object, _dbFactory);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var item = await verifyDb.WorkItems.FindAsync(id);
        item!.CompletedAt.Should().NotBeNull();
        item.ErrorMessage.Should().Be("Something broke");
    }

    // ── GET /api/work-items/{id}/assignment — Additional scenarios ─────

    [Fact]
    public async Task GetAssignment_Returns200_WhenWorkItemIsPending()
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(CreateTestPayload(), PipelineJsonOptions.Default);
        await SeedWorkItemAsync(id, WorkItemStatus.Pending, payload);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        result.Should().BeOfType<Ok<WorkItemAssignmentDto>>();
        var okResult = (Ok<WorkItemAssignmentDto>)result;
        okResult.Value.Should().NotBeNull();
        okResult.Value!.JobId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetAssignment_MapsAllDtoFields_FromPayload()
    {
        var id = Guid.NewGuid();
        var sourcePayload = CreateTestPayload();
        var payload = JsonSerializer.Serialize(sourcePayload, PipelineJsonOptions.Default);
        await SeedWorkItemAsync(id, WorkItemStatus.Running, payload);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        var okResult = (Ok<WorkItemAssignmentDto>)result;
        var dto = okResult.Value!;
        dto.JobId.Should().Be(id.ToString());
        dto.IssueIdentifier.Should().Be(sourcePayload.IssueIdentifier);
        dto.IssueProviderConfigId.Should().Be(sourcePayload.IssueProviderConfigId);
        dto.RepoProviderConfigId.Should().Be(sourcePayload.RepoProviderConfigId);
        dto.InitiatedBy.Should().Be(sourcePayload.InitiatedBy);
        dto.TaskType.Should().Be(sourcePayload.TaskType);
        dto.AgentSelector.Should().Be(sourcePayload.AgentSelector);
        dto.TimeoutSeconds.Should().Be(sourcePayload.TimeoutSeconds);
        dto.IssueDetail.Should().NotBeNull();
        dto.IssueDetail!.Title.Should().Be("Test Issue");
        dto.ParsedIssue.Should().NotBeNull();
        dto.IssueComments.Should().NotBeNull();
        dto.PipelineConfiguration.Should().NotBeNull();
        dto.QualityGateConfigs.Should().NotBeNull();
        dto.ReviewerConfigs.Should().NotBeNull();
    }

    [Theory]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task GetAssignment_ReturnsGone_ForAllTerminalStatuses(WorkItemStatus terminalStatus)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(CreateTestPayload(), PipelineJsonOptions.Default);
        await SeedWorkItemAsync(id, terminalStatus, payload);

        var result = await WorkItemEndpoints.GetAssignment(id, _dbFactory);

        result.Should().BeOfType<StatusCodeHttpResult>();
        ((StatusCodeHttpResult)result).StatusCode.Should().Be(410);
    }

    // ── POST /api/work-items/{id}/status — Additional scenarios ──────────

    [Fact]
    public async Task PostStatus_StoresResultOnSuccess()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Running);

        var resultJson = "{\"pullRequestUrl\":\"https://github.com/org/repo/pull/42\"}";
        var request = new WorkItemStatusRequest
        {
            Status = WorkItemStatus.Succeeded,
            AgentId = "agent-1",
            Result = resultJson
        };

        await WorkItemEndpoints.PostStatus(id, request, _transitionService, _runService.Object, _dbFactory);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var item = await verifyDb.WorkItems.FindAsync(id);
        item!.Result.Should().NotBeNull();
        item.Result.Should().Contain("pullRequestUrl");
        item.Result.Should().Contain("https://github.com/org/repo/pull/42");
    }

    [Fact]
    public async Task PostStatus_SetsCompletedAtOnSucceeded()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Running);
        var before = DateTimeOffset.UtcNow;

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded, AgentId = "agent-1" };

        await WorkItemEndpoints.PostStatus(id, request, _transitionService, _runService.Object, _dbFactory);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var item = await verifyDb.WorkItems.FindAsync(id);
        item!.CompletedAt.Should().NotBeNull();
        item.CompletedAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task PostStatus_SetsCompletedAtOnCancelled()
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, WorkItemStatus.Running);
        var before = DateTimeOffset.UtcNow;

        var request = new WorkItemStatusRequest { Status = WorkItemStatus.Cancelled, AgentId = "agent-1" };

        await WorkItemEndpoints.PostStatus(id, request, _transitionService, _runService.Object, _dbFactory);

        await using var verifyDb = _dbFactory.CreateDbContext();
        var item = await verifyDb.WorkItems.FindAsync(id);
        item!.CompletedAt.Should().NotBeNull();
        item.CompletedAt!.Value.Should().BeOnOrAfter(before);
    }

    [Theory]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Succeeded)]
    public async Task PostStatus_Returns400_OnVariousInvalidTransitions(WorkItemStatus from, WorkItemStatus to)
    {
        var id = Guid.NewGuid();
        await SeedWorkItemAsync(id, from);

        var request = new WorkItemStatusRequest { Status = to, AgentId = "agent-1" };

        var result = await WorkItemEndpoints.PostStatus(
            id, request, _transitionService, _runService.Object, _dbFactory);

        result.Should().BeOfType<BadRequest<string>>();
    }

    // ── JSON Enum Serialization (validates ConfigureHttpJsonOptions fix) ──

    [Fact]
    public void WorkItemStatusRequest_DeserializesEnumFromString()
    {
        // This test validates that the agent's JSON payload (enum-as-string)
        // correctly deserializes into WorkItemStatusRequest.
        // The agent sends: {"status": "Running", "agentId": "pod-xyz"}
        // Without JsonStringEnumConverter, this fails with a JsonException.
        var agentJson = """{"status": "Running", "agentId": "caa-pod-xyz"}""";

        // Use the same options the host configures via ConfigureHttpJsonOptions
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var request = JsonSerializer.Deserialize<WorkItemStatusRequest>(agentJson, options);

        request.Should().NotBeNull();
        request!.Status.Should().Be(WorkItemStatus.Running);
        request.AgentId.Should().Be("caa-pod-xyz");
    }

    [Theory]
    [InlineData("Running", WorkItemStatus.Running)]
    [InlineData("Succeeded", WorkItemStatus.Succeeded)]
    [InlineData("Failed", WorkItemStatus.Failed)]
    [InlineData("Cancelled", WorkItemStatus.Cancelled)]
    public void WorkItemStatusRequest_DeserializesAllEnumValues_FromString(string statusString, WorkItemStatus expected)
    {
        var json = $$"""{"status": "{{statusString}}", "agentId": "test"}""";

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var request = JsonSerializer.Deserialize<WorkItemStatusRequest>(json, options);

        request.Should().NotBeNull();
        request!.Status.Should().Be(expected);
    }

    [Fact]
    public void WorkItemStatusRequest_WithoutEnumConverter_FailsOnStringValue()
    {
        // Proves the bug: without JsonStringEnumConverter, "Running" can't deserialize
        var agentJson = """{"status": "Running", "agentId": "caa-pod-xyz"}""";

        var optionsWithoutConverter = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // No JsonStringEnumConverter — this is what was happening before the fix

        var act = () => JsonSerializer.Deserialize<WorkItemStatusRequest>(agentJson, optionsWithoutConverter);

        act.Should().Throw<JsonException>();
    }

    // ── Authentication enforcement ──────────────────────────────────────

    // NOTE: Authentication enforcement (RequireAuthorization("AgentApiKey")) is configured
    // via MapWorkItemEndpoints() which calls .RequireAuthorization("AgentApiKey") on the
    // endpoint group. Testing this requires an integration test with a real WebApplication
    // host (WebApplicationFactory) that exercises the middleware pipeline.
    // Unit tests here call the static handler methods directly and bypass auth middleware.
    // Integration-level auth testing is deferred to a dedicated integration test suite.

    // ── Helpers ──────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateTestPayload() => new()
    {
        IssueIdentifier = "owner/repo#1",
        IssueProviderConfigId = "ipc-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "system",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro",
        TimeoutSeconds = 3600,
        IssueDetail = new IssueDetail
        {
            Identifier = "1",
            Title = "Test Issue",
            Description = "Test body",
            Labels = []
        },
        ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
        IssueComments = [],
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        QualityGateConfigs = [],
        ReviewerConfigs = []
    };

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

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

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
