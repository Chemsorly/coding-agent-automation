using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Tests for the triage steps: <see cref="WriteTriageContextStep"/>, <see cref="TriageInvestigationStep"/> and
/// <see cref="ReportTriageResultStep"/>.
/// </summary>
public sealed class TriageStepsTests : IDisposable
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    private const string ValidResult = """
        {
          "verdict": "cause_found", "confidence": "high", "summary": "The worker acks before it updates the order.",
          "investigated": [ { "check": "Consumer", "where": "code · api", "result": "ack first" } ],
          "drafts": [ { "kind": "root_fix", "targetRepository": "checkout-api", "title": "Ack after the update", "body": "## Problem\nx" } ]
        }
        """;

    private readonly Mock<IAgentProvider> _agent = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly string _workspace;

    public TriageStepsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"triage-step-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workspace, ".agent"));

        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>())).Returns(Task.CompletedTask);

        _agent.Setup(p => p.GetHealthStatus()).Returns(new AgentHealthStatus { IsExecuting = true, IsProcessAlive = true });
        _agent.Setup(p => p.GetLatestSessionIdAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>())).ReturnsAsync("session");
        _agent.Setup(p => p.ProviderType).Returns(AgentProviderType.KiroCli);

        _issueOps.Setup(o => o.ReportTriageResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    // ── WriteTriageContextStep ───────────────────────────────────────────────

    [Fact]
    public async Task WriteContext_WithoutReport_FailsTheRun()
    {
        var context = Context();
        context.Issue = null;

        var result = await new WriteTriageContextStep("ctx").ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FailureCategory.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public async Task WriteContext_WritesTheReportAndTheTriageContext()
    {
        var context = Context();
        context.IssueComments = Enumerable.Range(1, 60)
            .Select(i => new IssueComment { Id = $"{i}", Author = "a", Body = $"comment {i}", CreatedAt = DateTime.UnixEpoch.AddMinutes(i) })
            .ToList();

        var result = await new WriteTriageContextStep("# Earlier attempts\n\nAttempt 1: inconclusive").ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var report = await File.ReadAllTextAsync(Path.Combine(_workspace, AgentWorkspacePaths.IssueContextFilePath));
        report.Should().Contain("Checkout returns 502").And.Contain("comment 60").And.NotContain("comment 10\n");
        (await File.ReadAllTextAsync(Path.Combine(_workspace, AgentWorkspacePaths.TriageContextFilePath)))
            .Should().Contain("Attempt 1: inconclusive");
    }

    [Fact]
    public async Task WriteContext_ListsTheOpenIssuesOfEveryTracker_SkippingTheTriagedIssue_AndToleratesAFailingTracker()
    {
        var context = Context(project: new DecompositionProjectContext
        {
            ProjectName = "Shop",
            Repositories =
            [
                Repo("checkout-api", "rp-api", "t-api"),
                Repo("storefront-web", "rp-web", "t-web"),
                Repo("payments-worker", "rp-pay", "t-pay"),
            ],
        });
        context.Issue = new IssueDetail
        {
            Identifier = "431",
            Title = "Checkout returns 502",
            Description = "Since 14:05 one in five checkouts fails.",
            Labels = [],
            Url = "https://tracker/431",
        };
        _issueOps.Setup(o => o.ListOpenIssuesForProviderAsync("t-api", 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(Summary("7", "Retry storm", "https://tracker/7"), Summary("431", "The report itself", "https://tracker/431")));
        _issueOps.Setup(o => o.ListOpenIssuesForProviderAsync("t-web", 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tracker down"));
        _issueOps.Setup(o => o.ListOpenIssuesForProviderAsync("t-pay", 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(Summary("3", "Refund twice", null)));

        var result = await new WriteTriageContextStep(null).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.OpenIssuesDownloaded.Should().Be(2);
        var files = Directory.GetFiles(Path.Combine(_workspace, AgentWorkspacePaths.OpenIssuesDirectory)).Select(Path.GetFileName);
        files.Should().BeEquivalentTo("checkout-api-7.md", "payments-worker-3.md");
    }

    // ── TriageInvestigationStep ──────────────────────────────────────────────

    [Fact]
    public async Task Investigation_ValidResultWithoutReview_ContinuesAndNormalisesTheFile()
    {
        var context = Context(config: Config(review: false));
        AgentWrites(ValidResult);

        var result = await new TriageInvestigationStep(reportToTracker: false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.Metrics.PhaseBreakdown.Should().ContainKey("triage");
        var normalised = JsonSerializer.Deserialize<TriageResult>(
            await File.ReadAllTextAsync(ResultPath), PipelineJsonOptions.Lenient)!;
        normalised.Drafts.Single().Id.Should().Be("d1");
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.Investigating), Times.Once);
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.ReviewingRca), Times.Never);
    }

    [Fact]
    public async Task Investigation_NoResultFile_FailsTheRun()
    {
        var context = Context(config: Config(review: false));
        AgentWrites(null);

        var result = await new TriageInvestigationStep(false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FailureReason.Should().Contain("did not produce a triage result");
    }

    [Fact]
    public async Task Investigation_ResultTooLarge_FailsTheRun()
    {
        var context = Context(config: Config(review: false));
        AgentWrites(ValidResult.Replace("\"summary\": \"", "\"summary\": \"" + new string('x', TriageConstants.MaxResultBytes), StringComparison.Ordinal));

        var result = await new TriageInvestigationStep(false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FailureReason.Should().Contain("too large");
    }

    [Fact]
    public async Task Investigation_CauseFoundWithoutChecks_FailsValidation()
    {
        var context = Context(config: Config(review: false));
        AgentWrites("""{ "verdict": "cause_found", "summary": "s", "investigated": [] }""");

        var result = await new TriageInvestigationStep(false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FailureReason.Should().Contain("lists nothing it investigated");
    }

    [Fact]
    public async Task Investigation_RefinementBreaksTheFile_KeepsTheFirstResult()
    {
        var context = Context(config: Config(review: true));
        var reviewPath = Path.Combine(_workspace, AgentWorkspacePaths.TriageReviewFilePath);
        var call = 0;
        _agent.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                switch (call++)
                {
                    case 0: File.WriteAllText(ResultPath, ValidResult); break;                      // investigation
                    case 1: File.WriteAllText(reviewPath, "[CRITICAL] E1 has no source"); break;   // review
                    default: File.WriteAllText(ResultPath, "{ broken"); break;                     // refinement
                }
                return Ok();
            });

        var result = await new TriageInvestigationStep(false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        call.Should().Be(3);
        TriageResultParser.Parse(await File.ReadAllTextAsync(ResultPath)).Result!.Verdict.Should().Be(TriageVerdict.CauseFound);
        context.Run.Metrics.PhaseBreakdown.Should().ContainKeys("triage", "triage_review", "triage_refinement");
    }

    [Fact]
    public async Task Investigation_AgentExitsNonZero_FailsTheRun()
    {
        var context = Context(config: Config(review: false));
        _agent.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 2, OutputLines = [] });

        var result = await new TriageInvestigationStep(false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FailureCategory.Should().Be(FailureReason.ExitCodeFailure);
    }

    // ── ReportTriageResultStep ───────────────────────────────────────────────

    [Fact]
    public async Task Report_OperatorTriage_ReportsToTheApiOnly()
    {
        var context = Context();
        await File.WriteAllTextAsync(ResultPath, ValidResult);

        var result = await new ReportTriageResultStep(reportToTracker: false).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(o => o.ReportTriageResultAsync(It.Is<string>(j => j.Contains("cause_found")), It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Run.FinalLabel.Should().BeNull();
    }

    [Fact]
    public async Task Report_TrackerTriage_PostsTheMaskedComment_AndSetsTheReviewLabel()
    {
        var context = Context();
        context.InjectedSecrets = new Dictionary<string, string> { ["DB_PASSWORD"] = "hunter2-secret" };
        await File.WriteAllTextAsync(ResultPath, ValidResult.Replace("acks before", "acks (hunter2-secret) before", StringComparison.Ordinal));
        string? posted = null;
        _issueOps.Setup(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => posted = body)
            .ReturnsAsync("c1");
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await new ReportTriageResultStep(reportToTracker: true).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        posted.Should().StartWith(TriageConstants.CommentMarker).And.NotContain("hunter2-secret").And.Contain("***");
        _issueOps.Verify(o => o.SwapLabelAsync(context.Run.IssueIdentifier, AgentLabels.TriageReview, It.IsAny<CancellationToken>()), Times.Once);
        context.Run.FinalLabel.Should().Be(AgentLabels.TriageReview);
    }

    [Fact]
    public async Task Report_TrackerTriage_UpdatesTheExistingRcaComment()
    {
        var context = Context();
        await File.WriteAllTextAsync(ResultPath, ValidResult);
        _issueOps.Setup(o => o.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>
            {
                new() { Id = "41", Author = "bot", Body = TriageConstants.CommentMarker + "\nold", CreatedAt = DateTime.UnixEpoch },
                new() { Id = "42", Author = "anna", Body = "please look at the worker", CreatedAt = DateTime.UnixEpoch },
            });
        _issueOps.Setup(o => o.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await new ReportTriageResultStep(reportToTracker: true).ExecuteAsync(context, CancellationToken.None);

        _issueOps.Verify(o => o.UpdateCommentAsync(context.Run.IssueIdentifier, 41, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Report_ApiRefusesTheResult_FailsBeforeAnythingIsPosted()
    {
        var context = Context();
        await File.WriteAllTextAsync(ResultPath, ValidResult);
        _issueOps.Setup(o => o.ReportTriageResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var result = await new ReportTriageResultStep(reportToTracker: true).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        _issueOps.Verify(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        context.Run.FinalLabel.Should().Be(AgentLabels.Error);
    }

    [Fact]
    public async Task Report_CommentFails_FailsTheRun()
    {
        var context = Context();
        await File.WriteAllTextAsync(ResultPath, ValidResult);
        _issueOps.Setup(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("rate limited"));

        var result = await new ReportTriageResultStep(reportToTracker: true).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
        context.Run.FinalLabel.Should().Be(AgentLabels.Error);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ResultPath => Path.Combine(_workspace, AgentWorkspacePaths.TriageResultFilePath);

    private void AgentWrites(string? json) =>
        _agent.Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                if (json is not null)
                    File.WriteAllText(ResultPath, json);
                return Ok();
            });

    private static AgentResult Ok() => new()
    {
        ExitCode = 0,
        OutputLines = [],
        Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 },
    };

    private static PipelineConfiguration Config(bool review) => new()
    {
        WorkspaceBaseDirectory = Path.GetTempPath(),
        AgentTimeout = TimeSpan.FromMinutes(30),
        StallPollInterval = TimeSpan.FromSeconds(30),
        StallWarningInterval = TimeSpan.FromMinutes(2),
        TriageReviewEnabled = review,
        MaxOpenIssuesForContext = 50,
    };

    private PipelineStepContext Context(PipelineConfiguration? config = null, DecompositionProjectContext? project = null)
    {
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "431",
            IssueTitle = "Checkout returns 502",
            IssueProviderConfigId = "",
            RepoProviderConfigId = "rp-api",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Triage,
            ProjectId = "p1",
            ProjectName = "Shop",
            WorkspacePath = _workspace,
        };
        return new PipelineStepContext
        {
            Run = run,
            Config = config ?? Config(review: false),
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _agent.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger,
            ProjectContext = project,
            Issue = new IssueDetail
            {
                Identifier = "431",
                Title = "Checkout returns 502",
                Description = "Since 14:05 one in five checkouts fails.",
                Labels = [],
            },
        };
    }

    private static RepositoryTarget Repo(string name, string repoId, string trackerId) => new()
    {
        TemplateName = name,
        Description = "",
        RepoProviderId = repoId,
        IssueProviderId = trackerId,
    };

    private static IssueSummary Summary(string id, string title, string? url) => new()
    {
        Identifier = id,
        Title = title,
        Labels = [],
        Url = url,
    };

    private static PagedResult<IssueSummary> Page(params IssueSummary[] items) => new()
    {
        Items = items,
        Page = 1,
        PageSize = 50,
        HasMore = false,
    };
}
