using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Triage;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// <see cref="TriageService"/> and <see cref="TriageIssueCreator"/>: the operator triage's identity payload and
/// executor, the project checks on every repository id, re-runs, draft edits, dismissal and issue creation.
/// </summary>
public sealed class TriageServiceTests
{
    private static readonly PipelineProject Project = new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Shop",
        AgentTimeout = TimeSpan.FromMinutes(45),
        Secrets = new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2-secret" },
    };

    private static readonly PipelineJobTemplate Api = Template("checkout-api", "rp-api", "t-api");
    private static readonly PipelineJobTemplate Web = Template("storefront-web", "rp-web", "t-web");

    private readonly DbContextOptions<PipelineDbContext> _db =
        new DbContextOptionsBuilder<PipelineDbContext>().UseInMemoryDatabase($"triage-{Guid.NewGuid():N}").Options;
    private readonly Mock<ITriageStore> _store = new();
    private readonly Mock<IConfigurationStore> _config = new();
    private readonly Mock<IOrchestratorRunService> _runs = new();
    private readonly Mock<TriageTrackerOperations> _tracker = new(Mock.Of<IConfigurationStore>(), Mock.Of<IProviderFactory>(),
        Mock.Of<ILabelService>(), Serilog.Log.Logger);
    private readonly Mock<IProviderFactory> _providers = new();
    private readonly Mock<IIssueProvider> _issues = new();
    private readonly Dictionary<Guid, TriageRecord> _records = [];

    public TriageServiceTests()
    {
        _config.Setup(c => c.GetProjectByIdAsync(Project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Project);
        _config.Setup(c => c.LoadTemplatesForProjectAsync(Project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Web, Api, Template("disabled", "rp-x", "t-x") with { Enabled = false }]);
        _config.Setup(c => c.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, ProviderKind kind, CancellationToken _) =>
                new ProviderConfig { Id = id, DisplayName = id, ProviderType = "GitHub", Kind = kind, RequiredLabels = ["dotnet"] });
        _config.Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineConfiguration());
        _config.Setup(c => c.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AgentProfile { Id = "p", DisplayName = "p", AgentProviderConfigId = "a", MatchLabels = ["dotnet"], Enabled = true }]);

        // The store mock keeps records in memory and applies updates
        _store.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _records.GetValueOrDefault(id));
        _store.Setup(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<Func<TriageRecord, TriageRecord?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, Func<TriageRecord, TriageRecord?> mutate, CancellationToken _) =>
            {
                if (!_records.TryGetValue(id, out var current)) return null;
                var changed = mutate(current);
                if (changed is null) return current;
                _records[id] = changed;
                return changed;
            });

        _providers.Setup(p => p.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_issues.Object);
        _issues.Setup(i => i.DisposeAsync()).Returns(ValueTask.CompletedTask);
    }

    private TriageService Service() => new(
        new Factory(_db), _store.Object, _config.Object, _runs.Object, _tracker.Object, Serilog.Log.Logger);

    private TriageIssueCreator Creator() => new(
        _store.Object, _config.Object, _providers.Object, new NoLocks(), _tracker.Object, Serilog.Log.Logger);

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_InsertsTheTriageAndItsFirstWorkItem_WithAnIdentityPayload()
    {
        var detail = await Service().CreateAsync(Request(), CancellationToken.None);

        await using var db = new Factory(_db).CreateDbContext();
        var triage = await db.Triages.SingleAsync();
        var workItem = await db.WorkItems.SingleAsync();
        triage.Id.Should().Be(detail.Record.Id);
        triage.KeyProviderConfigId.Should().Be(TriageConstants.ProviderConfigId);
        workItem.IssueIdentifier.Should().Be(TriageConstants.IssueIdentifierFor(detail.Record.Id));
        workItem.IssueProviderConfigId.Should().Be(TriageConstants.ProviderConfigId);
        workItem.TaskType.Should().Be(WorkItemTaskType.Triage);
        workItem.TimeoutSeconds.Should().Be(45 * 60, "the project's AgentTimeout override applies");
        workItem.PriorityWeight.Should().Be(100, "an operator triage is a manual dispatch");
        workItem.AgentSelector.Should().Be("dotnet");
        detail.Record.Attempts.Single().WorkItemId.Should().Be(workItem.Id.ToString());

        var payload = JsonSerializer.Deserialize<JobDistributionRequest>(workItem.Payload!, PipelineJsonOptions.Lenient)!;
        payload.RepoProviderConfigId.Should().Be("rp-api", "the first enabled template by name runs it");
        payload.RunType.Should().Be(PipelineRunType.Triage);
        payload.InitiatedBy.Should().Be(InitiatedByConstants.Manual);
        _runs.Verify(r => r.AddRun(It.Is<PipelineRun>(run => run.RunId == workItem.Id.ToString())), Times.Once);
    }

    [Fact]
    public async Task Create_StartInTemplate_RunsOnIt()
    {
        await Service().CreateAsync(Request(startIn: Web.Id), CancellationToken.None);

        await using var db = new Factory(_db).CreateDbContext();
        JsonSerializer.Deserialize<JobDistributionRequest>((await db.WorkItems.SingleAsync()).Payload!, PipelineJsonOptions.Lenient)!
            .RepoProviderConfigId.Should().Be("rp-web");
    }

    [Theory]
    [InlineData("other-project-template")]
    [InlineData("disabled-template")]
    public async Task Create_StartInTemplateOutsideTheProjectsEnabledTemplates_IsRefused(string templateId)
    {
        var act = () => Service().CreateAsync(Request(startIn: templateId), CancellationToken.None);

        (await act.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Create_MissingFields_UnknownProject_OrNoTemplate_AreRefused()
    {
        var missing = () => Service().CreateAsync(Request() with { Request = new TriageRequest { Title = "t", WhatHappened = " ", Expected = "e" } }, CancellationToken.None);
        var unknown = () => Service().CreateAsync(Request() with { ProjectId = Guid.NewGuid().ToString() }, CancellationToken.None);

        (await missing.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(400);
        (await unknown.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(404);

        _config.Setup(c => c.LoadTemplatesForProjectAsync(Project.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var noTemplate = () => Service().CreateAsync(Request(), CancellationToken.None);
        (await noTemplate.Should().ThrowAsync<TriageRequestException>()).Which.Message.Should().Contain("no enabled template");
    }

    // ── Re-run ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_OperatorTriage_AddsAnAttemptWithTheFeedback_AndAWorkItem()
    {
        var created = await Service().CreateAsync(Request(), CancellationToken.None);
        await CompleteAllWorkItemsAsync();
        _records[created.Record.Id] = created.Record;

        var detail = await Service().RerunAsync(created.Record.Id, new RerunTriageRequest
        {
            Feedback = "Look at the worker",
            Answers = [new TriageAnswer { Question = "When?", Answer = "Mornings" }, new TriageAnswer { Question = "Q2", Answer = " " }],
            Author = "ben",
        }, CancellationToken.None);

        detail.Record.Attempts.Should().HaveCount(2);
        var feedback = detail.Record.Attempts[1].Feedback!;
        feedback.Text.Should().Be("Look at the worker");
        feedback.Answers.Should().ContainSingle().Which.Answer.Should().Be("Mornings");
        await using var db = new Factory(_db).CreateDbContext();
        (await db.WorkItems.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Rerun_WhileAnAttemptRuns_IsRefused()
    {
        var triage = Operator();
        _records[triage.Id] = triage;
        _store.Setup(s => s.GetActiveWorkItemIdAsync(It.IsAny<TriageRecord>(), It.IsAny<CancellationToken>())).ReturnsAsync("w1");

        var act = () => Service().RerunAsync(triage.Id, new RerunTriageRequest { Author = "ben" }, CancellationToken.None);

        (await act.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Rerun_TrackerTriage_AsksTheLoopThroughTheIssue()
    {
        var triage = Tracker();
        _records[triage.Id] = triage;

        await Service().RerunAsync(triage.Id, new RerunTriageRequest { Feedback = "f", Author = "ben" }, CancellationToken.None);

        _tracker.Verify(t => t.RequestRerunAsync(triage, It.Is<TriageFeedback>(f => f.Text == "f"), It.IsAny<CancellationToken>()), Times.Once);
        await using var db = new Factory(_db).CreateDbContext();
        (await db.WorkItems.CountAsync()).Should().Be(0, "the loop dispatches a tracker triage");
    }

    // ── Drafts and dismissal ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateDraft_ToAnotherProjectRepository_IsSaved_ToAForeignOne_IsRefused()
    {
        var triage = Operator() with { Drafts = [Editable("d1", "checkout-api")] };
        _records[triage.Id] = triage;

        var saved = await Service().UpdateDraftAsync(triage.Id, "d1", Edit("STOREFRONT-WEB"), CancellationToken.None);
        var foreign = () => Service().UpdateDraftAsync(triage.Id, "d1", Edit("billing-of-another-project"), CancellationToken.None);

        saved.Record.Drafts.Single().Current.TargetRepository.Should().Be("storefront-web");
        saved.Record.Drafts.Single().Edited.Should().BeTrue();
        (await foreign.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ResetDraft_RestoresTheAgentsVersion()
    {
        var triage = Operator() with { Drafts = [Editable("d1", "checkout-api")] };
        _records[triage.Id] = triage;
        await Service().UpdateDraftAsync(triage.Id, "d1", Edit("storefront-web"), CancellationToken.None);

        var reset = await Service().ResetDraftAsync(triage.Id, "d1", CancellationToken.None);

        reset.Record.Drafts.Single().Current.TargetRepository.Should().Be("checkout-api");
        reset.Record.Drafts.Single().Edited.Should().BeFalse();
    }

    [Fact]
    public async Task Dismiss_TrackerTriage_SetsTheStateAndTellsTheIssue()
    {
        var triage = Tracker();
        _records[triage.Id] = triage;

        var detail = await Service().DismissAsync(triage.Id, new DismissTriageRequest { Reason = "known", By = "anna" }, CancellationToken.None);

        detail.Status.Should().Be(TriageStatus.Dismissed);
        _tracker.Verify(t => t.ReportDismissedAsync(It.Is<TriageRecord>(r => r.DismissReason == "known"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Issue creation ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIssues_CreatesTheSelectedDrafts_InTheTargetTrackers_MaskedAndLabelled()
    {
        var triage = Operator() with { Drafts = [Editable("d1", "checkout-api", body: "uses hunter2-secret"), Editable("d2", "storefront-web")] };
        _records[triage.Id] = triage;
        var created = new List<(string Title, string Body, IReadOnlyList<string> Labels)>();
        _issues.Setup(i => i.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, string b, IReadOnlyList<string> l, CancellationToken _) =>
            {
                created.Add((t, b, l));
                return new CreatedIssueResult { Identifier = $"{created.Count}", Url = $"https://tracker/{created.Count}" };
            });

        var result = await Creator().CreateAsync(triage.Id, new CreateTriageIssuesRequest { DraftIds = ["d1"], Queue = true, CreatedBy = "ben" }, CancellationToken.None);

        result.Errors.Should().BeEmpty();
        result.Created.Should().ContainSingle().Which.Repository.Should().Be("checkout-api");
        created.Single().Body.Should().NotContain("hunter2-secret").And.Contain("Proposed by the triage");
        created.Single().Labels.Should().BeEquivalentTo(AgentLabels.Generated, AgentLabels.Next);
        _providers.Verify(p => p.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "t-api")), Times.Once);
        _records[triage.Id].CreatedIssues.Should().ContainSingle();
        _records[triage.Id].State.Should().Be(TriageState.IssuesCreated);
        _tracker.Verify(t => t.ReportCreatedAsync(It.IsAny<TriageRecord>(), It.IsAny<IReadOnlyList<TriageCreatedIssue>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateIssues_AlreadyCreatedDraft_IsNotCreatedAgain()
    {
        var triage = Operator() with
        {
            Drafts = [Editable("d1", "checkout-api")],
            CreatedIssues = [new TriageCreatedIssue { DraftId = "d1", Repository = "checkout-api", IssueProviderConfigId = "t-api", Identifier = "7", CreatedAt = DateTimeOffset.UtcNow }],
        };
        _records[triage.Id] = triage;

        var result = await Creator().CreateAsync(triage.Id, new CreateTriageIssuesRequest { DraftIds = ["d1", "d1"], CreatedBy = "ben" }, CancellationToken.None);

        result.Created.Should().ContainSingle().Which.Identifier.Should().Be("7");
        _issues.Verify(i => i.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateIssues_ReportsPerDraftErrors_AndCreatesTheRest()
    {
        var triage = Operator() with
        {
            Drafts = [Editable("d1", "repository-that-left-the-project"), Editable("d2", "checkout-api"), Editable("d3", "storefront-web")],
        };
        _records[triage.Id] = triage;
        var calls = 0;
        _issues.Setup(i => i.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 1
                ? throw new InvalidOperationException("tracker down")
                : new CreatedIssueResult { Identifier = "9", Url = "https://tracker/9" });

        var result = await Creator().CreateAsync(triage.Id,
            new CreateTriageIssuesRequest { DraftIds = ["d1", "d2", "d3", "nope"], CreatedBy = "ben" }, CancellationToken.None);

        result.Created.Select(c => c.DraftId).Should().Equal("d3");
        result.Errors.Select(e => e.DraftId).Should().BeEquivalentTo("d1", "d2", "nope");
    }

    [Fact]
    public async Task CreateIssues_TrackerTriage_SummarisesOnTheIssue()
    {
        var triage = Tracker() with { Drafts = [Editable("d1", "checkout-api")] };
        _records[triage.Id] = triage;
        _issues.Setup(i => i.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult { Identifier = "9", Url = "https://tracker/9" });

        await Creator().CreateAsync(triage.Id, new CreateTriageIssuesRequest { DraftIds = ["d1"], CreatedBy = "ben" }, CancellationToken.None);

        _tracker.Verify(t => t.ReportCreatedAsync(It.IsAny<TriageRecord>(), It.Is<IReadOnlyList<TriageCreatedIssue>>(c => c.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateIssues_WhileAnAttemptRuns_IsRefused()
    {
        var triage = Operator() with { Drafts = [Editable("d1", "checkout-api")] };
        _records[triage.Id] = triage;
        _store.Setup(s => s.GetActiveWorkItemIdAsync(It.IsAny<TriageRecord>(), It.IsAny<CancellationToken>())).ReturnsAsync("w");

        var act = () => Creator().CreateAsync(triage.Id, new CreateTriageIssuesRequest { DraftIds = ["d1"], CreatedBy = "ben" }, CancellationToken.None);

        (await act.Should().ThrowAsync<TriageRequestException>()).Which.StatusCode.Should().Be(409);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task CompleteAllWorkItemsAsync()
    {
        await using var db = new Factory(_db).CreateDbContext();
        foreach (var w in db.WorkItems)
            w.Status = WorkItemStatus.Succeeded;
        await db.SaveChangesAsync();
    }

    private static CreateTriageRequest Request(string? startIn = null) => new()
    {
        ProjectId = Project.Id,
        RequestedBy = "anna",
        Request = new TriageRequest
        {
            Title = "Checkout returns 502",
            WhatHappened = "502 on pay",
            Expected = "200",
            StartInTemplateId = startIn == "disabled-template" ? "tpl-disabled" : startIn,
        },
    };

    private static PipelineJobTemplate Template(string name, string repo, string tracker) => new()
    {
        Id = $"tpl-{name}",
        Name = name,
        RepoProviderId = repo,
        IssueProviderId = tracker,
        Enabled = true,
    };

    private static TriageRecord Operator() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Project.Id,
        Source = TriageSource.Operator,
        Title = "Orders stuck",
        Request = new TriageRequest { Title = "Orders stuck", WhatHappened = "w", Expected = "e" },
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TriageRecord Tracker() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Project.Id,
        Source = TriageSource.Issue,
        IssueProviderConfigId = "t-web",
        IssueIdentifier = "431",
        IssueUrl = "https://tracker/431",
        Title = "Order confirmation spins",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TriageEditableDraft Editable(string id, string repository, string body = "## Problem\nx")
    {
        var draft = new TriageDraft { Id = id, TargetRepository = repository, Title = $"Fix {id}", Body = body };
        return new TriageEditableDraft { Current = draft, Original = draft };
    }

    private static UpdateTriageDraftRequest Edit(string repository) => new()
    {
        Kind = TriageDraftKind.Mitigation,
        TargetRepository = repository,
        Title = "Edited",
        Body = "Edited body",
        EditedBy = "ben",
    };

    private sealed class NoLocks : IDistributedLockProvider
    {
        public Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new Released());

        private sealed class Released : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<PipelineDbContext> options) : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new InMemoryContext(options);
    }

    private sealed class InMemoryContext(DbContextOptions<PipelineDbContext> options) : PipelineDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entity.FindProperty("RowVersion");
                if (rowVersion is not null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
        }
    }
}
