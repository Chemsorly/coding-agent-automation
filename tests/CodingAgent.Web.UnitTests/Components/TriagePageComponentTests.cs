using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using static CodingAgent.Web.UnitTests.Components.TriageTestData;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The triage page (spec 050, B4/B6): the report, the drafts and the actions, which need operator on the
/// project stored on the triage.
/// </summary>
public class TriagePageComponentTests : BunitContext
{
    private readonly Mock<CodingAgent.Web.Ui.IUiTriageClient> _triages = new();
    private readonly Mock<CodingAgent.Web.Ui.IUiConfigClient> _config = new();
    private readonly Mock<CodingAgent.Web.Ui.IUiRunClient> _runHistory = new();

    public TriagePageComponentTests()
    {
        _config.Setup(c => c.GetPipelineSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgent.Contracts.Ui.UiPipelineSettings(3, new PipelineConfiguration().TriageRetentionDays));
        _config.Setup(c => c.GetTemplatesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new CodingAgent.Contracts.Ui.UiTemplate("t1", "payments-worker", true),
                new CodingAgent.Contracts.Ui.UiTemplate("t2", "checkout-api", true),
            ]);
        // Default: GetRunAsync returns null (old runs / no run data)
        _runHistory.Setup(r => r.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CodingAgent.Contracts.Ui.UiRunDetail?)null);
        Services.AddSingleton(_triages.Object);
        Services.AddSingleton(_config.Object);
        Services.AddSingleton(_runHistory.Object);
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    private void Returns(TriageDetail detail) =>
        _triages.Setup(t => t.GetAsync(TriageId, It.IsAny<CancellationToken>())).ReturnsAsync(detail);

    private IRenderedComponent<TriagePage> RenderPage(AccessGrant? grant = null)
    {
        Services.AddTestAccess(grant ?? TestAccess.Scoped((ShopProjectId, AccessRole.Operator)));
        return Render<TriagePage>(p => p.Add(c => c.Id, TriageId));
    }

    private static TriageDetail CauseFoundDetail() =>
        Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound(Draft("d1", "Ack after the update"), Draft("d2", "Raise the memory limit")))));

    [Fact]
    public void CauseFound_ShowsTheReportWhatWasInvestigatedAndTheDrafts()
    {
        Returns(CauseFoundDetail());

        var cut = RenderPage();

        cut.Find("[data-testid='triage-status']").TextContent.Should().Contain("RCA ready");
        cut.Find("[data-testid='triage-verdict']").TextContent.Should().Contain("The worker acknowledges the message");
        cut.Find("[data-testid='triage-chain']").TextContent.Should().Contain("Root cause");
        cut.Find("[data-testid='triage-investigated']").TextContent.Should().Contain("Pod restarts").And.Contain("Traces");
        cut.FindAll("[data-testid='triage-draft']").Should().HaveCount(2);
        cut.Find("[data-testid='triage-checks-by-source']").TextContent.Should().Contain("grafana").And.Contain("2 checks");
        cut.Find("[data-testid='triage-request']").TextContent.Should().Contain("Customers paid");
    }

    [Fact]
    public void EvidenceLinks_OnlyHttpIsRenderedAsALink()
    {
        Returns(CauseFoundDetail());

        var cut = RenderPage();

        var links = cut.Find("[data-testid='triage-evidence']").QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        links.Should().Equal("https://grafana.example/explore");
    }

    [Fact]
    public void CreateIssues_SendsTheSelectedDraftsAndTheQueueFlag()
    {
        Returns(CauseFoundDetail());
        _triages.Setup(t => t.CreateIssuesAsync(TriageId, It.IsAny<CreateTriageIssuesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTriageIssuesResult());
        var cut = RenderPage();

        cut.FindAll("[data-testid='triage-draft-select']")[1].Change(false);
        cut.Find("[data-testid='triage-queue']").Change(true);
        cut.Find("[data-testid='triage-create']").TextContent.Should().Contain("Create 1 issue");
        cut.Find("[data-testid='triage-create']").Click();

        _triages.Verify(t => t.CreateIssuesAsync(TriageId,
            It.Is<CreateTriageIssuesRequest>(r => r.DraftIds.SequenceEqual(new[] { "d1" }) && r.Queue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ReadOnlyUser_SeesTheReportButNoActions()
    {
        Returns(CauseFoundDetail());

        var cut = RenderPage(TestAccess.Scoped((ShopProjectId, AccessRole.ReadOnly)));

        cut.Find("[data-testid='triage-verdict']");
        cut.FindAll("[data-testid='triage-actions']").Should().BeEmpty();
        cut.FindAll("[data-testid='triage-draft-select']").Should().BeEmpty();
        cut.FindAll("[data-testid='triage-draft-edit']").Should().BeEmpty();
    }

    [Fact]
    public void TriageOfAProjectTheUserCannotRead_LooksMissing()
    {
        Returns(Detail(OperatorRecord(OtherProjectId, Attempt("wi-1", CauseFound()))));

        var cut = RenderPage();

        cut.Find("[data-testid='triage-not-found']");
        cut.FindAll("[data-testid='triage-verdict']").Should().BeEmpty();
    }

    [Fact]
    public void OperatorOfAnotherProject_CannotActOnThisOne()
    {
        // The role check uses the project stored on the triage, not the user's other bindings
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound(Draft("d1", "Fix"))))));

        var cut = RenderPage(TestAccess.Scoped((ShopProjectId, AccessRole.ReadOnly), (OtherProjectId, AccessRole.Operator)));

        cut.FindAll("[data-testid='triage-actions']").Should().BeEmpty();
    }

    [Fact]
    public void NeedsInput_AnswersGoIntoTheReRun()
    {
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", Inconclusive()))));
        _triages.Setup(t => t.RerunAsync(TriageId, It.IsAny<RerunTriageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", Inconclusive()), Attempt("wi-2")), "wi-2"));
        var cut = RenderPage();

        cut.Find("#triage-answer-0").Input("The redirect back from the identity provider");
        cut.Find("[data-testid='triage-rerun-answers']").Click();

        _triages.Verify(t => t.RerunAsync(TriageId,
            It.Is<RerunTriageRequest>(r => r.Answers.Count == 1
                && r.Answers[0].Question.StartsWith("Is the login page slow")
                && r.Answers[0].Answer == "The redirect back from the identity provider"),
            It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid='triage-running']");
    }

    [Fact]
    public void Investigating_LinksTheRunPageAndOffersNoActions()
    {
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1")), "wi-1"));

        var cut = RenderPage();

        cut.Find("[data-testid='triage-running']").TextContent.Should().Contain("investigating");
        cut.Find("[data-testid='triage-run-link']").GetAttribute("href").Should().Be("runs/wi-1");
        cut.FindAll("[data-testid='triage-actions']").Should().BeEmpty();
    }

    [Fact]
    public void FailedAttempt_ShowsTheReasonAndKeepsTheEarlierResult()
    {
        Returns(Detail(OperatorRecord(ShopProjectId,
            Attempt("wi-1", CauseFound(Draft("d1", "Fix"))),
            Attempt("wi-2", outcome: TriageAttemptOutcome.Failed, failure: "Agent timeout after 30 min"))));

        var cut = RenderPage();

        cut.Find("[data-testid='triage-status']").TextContent.Should().Contain("Failed");
        cut.Find("[data-testid='triage-failed']").TextContent.Should().Contain("Agent timeout after 30 min").And.Contain("attempt 1");
        cut.Find("[data-testid='triage-verdict']");
    }

    [Fact]
    public void RerunWithFeedback_SendsTheText()
    {
        Returns(CauseFoundDetail());
        _triages.Setup(t => t.RerunAsync(TriageId, It.IsAny<RerunTriageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CauseFoundDetail());
        var cut = RenderPage();

        cut.Find("[data-testid='triage-rerun-open']").Click();
        cut.Find("[data-testid='triage-feedback']").Input("Look at the worker's memory limit");
        cut.Find("[data-testid='triage-rerun']").Click();

        _triages.Verify(t => t.RerunAsync(TriageId,
            It.Is<RerunTriageRequest>(r => r.Feedback == "Look at the worker's memory limit"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Dismiss_SendsTheReason()
    {
        Returns(CauseFoundDetail());
        _triages.Setup(t => t.DismissAsync(TriageId, It.IsAny<DismissTriageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CauseFoundDetail());
        var cut = RenderPage();

        cut.Find("[data-testid='triage-dismiss-open']").Click();
        cut.Find("[data-testid='triage-dismiss-reason']").Change("Known, tracked in Jira");
        cut.Find("[data-testid='triage-dismiss']").Click();

        _triages.Verify(t => t.DismissAsync(TriageId,
            It.Is<DismissTriageRequest>(r => r.Reason == "Known, tracked in Jira"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ApiRefusal_IsShownToTheUser()
    {
        Returns(CauseFoundDetail());
        _triages.Setup(t => t.CreateIssuesAsync(TriageId, It.IsAny<CreateTriageIssuesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("An attempt is running"));
        var cut = RenderPage();

        cut.Find("[data-testid='triage-create']").Click();

        cut.Find("[data-testid='triage-error']").TextContent.Should().Contain("An attempt is running");
    }

    [Fact]
    public void EditDraft_SavesTheEditedValues()
    {
        Returns(CauseFoundDetail());
        _triages.Setup(t => t.UpdateDraftAsync(TriageId, "d1", It.IsAny<UpdateTriageDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CauseFoundDetail());
        var cut = RenderPage();

        cut.FindAll("[data-testid='triage-draft-edit']")[0].Click();
        cut.Find("[data-testid='triage-draft-repo']").Change("checkout-api");
        cut.Find("[data-testid='triage-draft-title']").Change("Acknowledge after the order is paid");
        cut.Find("[data-testid='triage-draft-save']").Click();

        _triages.Verify(t => t.UpdateDraftAsync(TriageId, "d1",
            It.Is<UpdateTriageDraftRequest>(r => r.TargetRepository == "checkout-api"
                && r.Title == "Acknowledge after the order is paid"
                && r.Body == "Move the ack after MarkPaidAsync."),
            It.IsAny<CancellationToken>()), Times.Once);
        cut.FindAll("[data-testid='triage-draft-editor']").Should().BeEmpty("the drawer closes after saving");
    }

    [Fact]
    public void CreatedDraft_ShowsItsIssueAndCannotBeSelectedAgain()
    {
        var record = OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound(Draft("d1", "Fix"), Draft("d2", "Mitigate")))) with
        {
            CreatedIssues =
            [
                new TriageCreatedIssue
                {
                    DraftId = "d1", Repository = "payments-worker", IssueProviderConfigId = "ip-1", Identifier = "77",
                    Url = "https://github.com/acme/payments-worker/issues/77", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "ben",
                },
            ],
        };
        Returns(Detail(record));

        var cut = RenderPage();

        cut.Find("[data-testid='triage-created-link']").GetAttribute("href").Should().Be("https://github.com/acme/payments-worker/issues/77");
        cut.FindAll("[data-testid='triage-draft-select']").Should().HaveCount(1);
        cut.Find("[data-testid='triage-create']").TextContent.Should().Contain("Create 1 issue");
        cut.Find("[data-testid='triage-created-issues']").TextContent.Should().Contain("payments-worker #77");
    }

    [Fact]
    public void TrackerTriage_Rerun_SaysTheLoopStartsIt_AndOffersNoSecondRequest()
    {
        // The loop starts a tracker triage's attempt from the issue's label, so the returned record shows no new attempt
        var tracker = OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound(Draft("d1", "Fix")))) with
        {
            Source = TriageSource.Issue,
            IssueProviderConfigId = "ip-1",
            IssueIdentifier = "431",
            Request = null,
        };
        Returns(Detail(tracker));
        _triages.Setup(t => t.RerunAsync(TriageId, It.IsAny<RerunTriageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail(tracker));
        var cut = RenderPage();

        cut.Find("[data-testid='triage-rerun-open']").Click();
        cut.Find("[data-testid='triage-feedback']").Input("Check the retry policy");
        cut.Find("[data-testid='triage-rerun']").Click();

        cut.Find("[data-testid='triage-notice']").TextContent.Should().Contain("issue #431").And.Contain("agent:triage");
        cut.FindAll("[data-testid='triage-actions']").Should().BeEmpty();
    }

    [Fact]
    public void NeedsInput_ReadOnlyUser_SeesTheQuestionsAsAList()
    {
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", Inconclusive()))));

        var cut = RenderPage(TestAccess.Scoped((ShopProjectId, AccessRole.ReadOnly)));

        cut.Find("[data-testid='triage-report-questions']").TextContent.Should().Contain("Is the login page slow");
        cut.FindAll("[data-testid='triage-questions']").Should().BeEmpty();
    }

    [Fact]
    public void NeedsInput_Operator_SeesTheQuestionsOnlyAsTheForm()
    {
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", Inconclusive()))));

        var cut = RenderPage();

        cut.Find("[data-testid='triage-questions']");
        cut.FindAll("[data-testid='triage-report-questions']").Should().BeEmpty();
    }

    [Fact]
    public void LongDraftBody_IsCollapsedUntilExpanded()
    {
        var longDraft = Draft("d1", "Fix") with { Body = new string('x', 900) };
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound(longDraft, Draft("d2", "Short"))))));
        var cut = RenderPage();

        cut.FindAll("[data-testid='triage-draft-expand']").Should().ContainSingle("only the long body is collapsed");
        cut.Find("[data-testid='triage-draft-expand']").Click();

        cut.FindAll("[data-testid='triage-draft-expand']").Should().BeEmpty();
    }

    // ── Issue #3823: MCP call counts next to checks-by-source ────────────

    private static readonly string TriageRunId = "a0000000-0000-0000-0000-000000000001";

    private PipelineRunSummary MakeTriageRun(
        IReadOnlyList<string>? mcpServers = null,
        IReadOnlyDictionary<string, PhaseUsage>? phases = null) =>
        new PipelineRunSummary
        {
            RunId = TriageRunId,
            IssueIdentifier = "1",
            IssueTitle = "test triage run",
            FinalStep = PipelineStep.Completed,
            McpServers = mcpServers,
            PhaseBreakdown = phases,
        };

    private void ReturnsWithRun(TriageDetail detail, PipelineRunSummary run)
    {
        Returns(detail);
        _runHistory.Setup(r => r.GetRunAsync(Guid.Parse(TriageRunId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiRunDetail(run, new UiRunAgent(null, null, null)));
    }

    [Fact]
    public void ChecksBySource_WhenRunHasMcpCallsMatchingSource_ShowsCallCount()
    {
        var detail = Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound())));
        var run = MakeTriageRun(
            mcpServers: ["grafana"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["triage"] = new PhaseUsage(0, null) with
                {
                    ToolCalls = new Dictionary<string, McpCallCounts>
                    {
                        ["grafana/query"] = new McpCallCounts { Calls = 5 },
                    },
                },
            });
        ReturnsWithRun(detail, run);

        var cut = RenderPage();

        var checksSection = cut.Find("[data-testid='triage-checks-by-source']");
        // "grafana" row should show "5 call(s)"
        checksSection.TextContent.Should().Contain("5 call(s)");
    }

    [Fact]
    public void ChecksBySource_ConfiguredServerWithNoTriageCalls_ShowsNoCallsWarning()
    {
        var detail = Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound())));
        var run = MakeTriageRun(mcpServers: ["grafana"]);
        ReturnsWithRun(detail, run);

        var cut = RenderPage();

        cut.Find("[data-testid='triage-source-no-calls']").TextContent.Should().Contain("⚠");
        cut.Find("[data-testid='triage-source-no-calls']")
            .GetAttribute("title").Should()
            .Contain("The agent lists checks against this source, but the run made no call to it.");
    }

    [Fact]
    public void ChecksBySource_CallsInTriageReviewPhaseDoNotCount()
    {
        // triage_review calls must be excluded; only triage + triage_refinement count
        var detail = Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound())));
        var run = MakeTriageRun(
            mcpServers: ["grafana"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["triage_review"] = new PhaseUsage(0, null) with
                {
                    ToolCalls = new Dictionary<string, McpCallCounts>
                    {
                        ["grafana/query"] = new McpCallCounts { Calls = 50 },
                    },
                },
            });
        ReturnsWithRun(detail, run);

        var cut = RenderPage();

        // The triage_review calls don't count, so grafana has 0 triage-phase calls → no-calls warning
        cut.FindAll("[data-testid='triage-source-no-calls']").Should().HaveCount(1);
        cut.Find("[data-testid='triage-checks-by-source']").TextContent.Should().NotContain("50");
    }

    [Fact]
    public void ChecksBySource_NonServerSourceIsUnchanged()
    {
        // "code" appears in the CauseFound data but is not an MCP server.
        // mcpServers = ["grafana"] with no calls → grafana gets no-calls warning (1 element).
        // "code" (from TriageTestData checks "code · payments-worker") must not get the warning.
        var detail = Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound())));
        var run = MakeTriageRun(mcpServers: ["grafana"]);
        ReturnsWithRun(detail, run);

        var cut = RenderPage();

        // Only grafana (the configured server) should have the no-calls warning, not "code"
        cut.FindAll("[data-testid='triage-source-no-calls']").Should().HaveCount(1,
            "only the matched MCP server source ('grafana') gets the warning; 'code' must not");
    }

    [Fact]
    public void ChecksBySource_UnavailableServerShowsUnavailableStatus()
    {
        // When the matched server was unavailable in the triage phases, show the status instead of no-calls.
        var detail = Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound())));
        var run = MakeTriageRun(
            mcpServers: ["grafana"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["triage"] = new PhaseUsage(0, null) with
                {
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["grafana"] = "needs-auth",
                    },
                },
            });
        ReturnsWithRun(detail, run);

        var cut = RenderPage();

        var checksSection = cut.Find("[data-testid='triage-checks-by-source']");
        checksSection.TextContent.Should().Contain("unavailable (needs-auth)");
        // The no-calls warning must NOT render when unavailable supersedes it
        cut.FindAll("[data-testid='triage-source-no-calls']").Should().BeEmpty();
    }

    [Fact]
    public void ChecksBySource_WhenGetRunAsyncFails_PageStillRenders()
    {
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt(TriageRunId, CauseFound()))));
        _runHistory.Setup(r => r.GetRunAsync(Guid.Parse(TriageRunId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var cut = RenderPage();

        // Page renders normally; checks-by-source shows count only (no call info)
        cut.Find("[data-testid='triage-checks-by-source']");
        cut.FindAll("[data-testid='triage-source-no-calls']").Should().BeEmpty();
        cut.FindAll("[data-testid='triage-error']").Should().BeEmpty();
    }

    [Fact]
    public void ChecksBySource_WhenResultRunIsNull_SourcesShowOnlyCheckCount()
    {
        // _runHistory returns null by default → _resultRun is null → no enrichment
        Returns(Detail(OperatorRecord(ShopProjectId, Attempt("wi-1", CauseFound()))));

        var cut = RenderPage();

        var section = cut.Find("[data-testid='triage-checks-by-source']");
        section.TextContent.Should().Contain("grafana");
        cut.FindAll("[data-testid='triage-source-no-calls']").Should().BeEmpty();
    }
}
