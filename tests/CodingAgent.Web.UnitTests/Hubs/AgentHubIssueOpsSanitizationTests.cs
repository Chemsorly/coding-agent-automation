using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Log-injection regression tests for the in-memory-run path of
/// <see cref="AgentHub.RequestLabelChange"/>.
/// Verifies that <c>SanitizeForLog</c> is applied to the <c>newLabel</c> argument at:
/// <list type="bullet">
///   <item>the gated-label Warning branch (AgentHub.IssueOps.cs)</item>
///   <item>the success Information log (AgentHub.IssueOps.cs)</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Known limitation — first acceptance criterion cannot be mechanically satisfied:</b>
/// The first acceptance criterion for Issue #3702 requires "a test that calls
/// RequestLabelChange with a gated newLabel value containing a CRLF sequence … this test
/// must fail on the pre-fix code and pass after the fix." This requirement is structurally
/// impossible given the current code:
/// </para>
/// <para>
/// Both fixed log sites are gated behind <c>AgentLabels.All.Contains(newLabel)</c> /
/// <c>AgentLabels.DispatchGatedLabels.Contains(newLabel)</c> — exact-match lookups on a
/// fixed, CRLF-free label vocabulary. A CRLF-bearing string (e.g.
/// <c>"agent:epic-approved\r\nINFO: forged"</c>) is NOT a member of either set and is
/// rejected by the invalid-label guard, which already uses <c>SanitizeForLog</c> in the
/// pre-fix code. Therefore no CRLF-bearing <c>newLabel</c> reaches either of the two
/// newly-fixed log sites.
/// </para>
/// <para>
/// For any clean label that does reach those sites (the only possibility),
/// <c>SanitizeForLog(clean) == clean</c>, making the mock-logger invocations identical
/// before and after the fix. No Moq <c>Verify</c> assertion can distinguish pre-fix from
/// post-fix in this scenario.
/// </para>
/// <para>
/// These tests therefore anchor the structural wrapping convention and provide maximum
/// possible coverage given the constraint. The primary verification for the fix is the
/// <c>git grep</c> acceptance criterion (no raw <c>newLabel</c> in a logging argument
/// position in <c>AgentHub.IssueOps.cs</c>) and code review.
/// </para>
/// </remarks>
// TODO (WARNING — TestQualityReviewer / SecurityReviewer): Both tests in this class use
// clean label constants (AgentLabels.EpicApproved, AgentLabels.Error), for which
// SanitizeForLog returns the unchanged value. The Moq assertions therefore pass
// identically on pre-fix and post-fix code — reverting either SanitizeForLog wrapping
// would not cause either test to fail. This is a structural impossibility: CRLF-bearing
// labels are rejected by AgentLabels.All.Contains before reaching either log site, so
// a non-tautological test cannot be written without changing the production code's
// validation structure. The class-level remarks document this fully. The git grep check
// (AC2) is the primary regression guard.
public sealed class AgentHubIssueOpsSanitizationTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<IHubIssueOperations> _mockIssueOps = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub(string connectionId = "conn-1")
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            _mockIssueOps.Object,
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns(connectionId);
        hub.Context = mockContext.Object;

        return hub;
    }

    private static PipelineRun CreateRun(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1"
    };

    // ── Gated-label Warning: label arg must be the sanitized form ─────────
    // Path: RequestLabelChange → GetRun returns run → DispatchGatedLabels.Contains → Warning log

    [Fact]
    public async Task RequestLabelChange_GatedLabel_WarningLogArgumentIsSanitizedForm()
    {
        // Arrange: in-memory run present; gated label triggers the Warning branch.
        // AgentLabels.DispatchGatedLabels contains only EpicApproved ("agent:epic-approved").
        // SanitizeForLog("agent:epic-approved") == "agent:epic-approved" (no CRLF to escape).
        //
        // TODO (WARNING — TestQualityReviewer / SecurityReviewer): This test passes identically
        // on pre-fix code because SanitizeForLog(AgentLabels.EpicApproved) == AgentLabels.EpicApproved.
        // No test can fail on pre-fix code for this log site: a CRLF-bearing label (e.g.
        // "agent:epic-approved\r\nINFO: forged") is not a member of DispatchGatedLabels and is
        // rejected by the AgentLabels.All guard before reaching this branch. This is a structural
        // impossibility — see class-level remarks. The test anchors the SanitizeForLog wrapping
        // convention; the git grep AC2 check is the regression guard.
        const string jobId = "job-gated";
        var run = CreateRun(jobId);
        _mockFacade.Setup(f => f.GetRun(new JobId(jobId))).Returns(run);

        var hub = CreateHub();

        // Act
        await hub.RequestLabelChange(jobId, AgentLabels.EpicApproved);

        // Assert: Warning was emitted with the sanitized label value at the first arg position.
        // Warning<string,string>(template, SanitizeForLog(newLabel), jobId.Value)
        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(t => t.Contains("gated label")),
                It.Is<string>(s => s == LogSanitizer.SanitizeForLog(AgentLabels.EpicApproved)),
                jobId),
            Times.Once,
            "gated-label Warning must pass SanitizeForLog(newLabel) as the label argument");

        // Assert: raw CRLF never appeared in any Warning call for this operation.
        // Vacuously true for clean label constants but documents intent and catches future
        // regressions if a CRLF-bearing path is added.
        // TODO (WARNING — TestQualityReviewer): This Times.Never assertion is vacuously true:
        // AgentLabels.All.Contains rejects any CRLF-bearing label before reaching the
        // gated-label Warning branch, so no reachable input can trigger it. The assertion
        // documents the absence-of-injection expectation but provides no regression protection.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.Is<string>(s => s.Contains('\r') || s.Contains('\n')),
                It.IsAny<string>()),
            Times.Never,
            "no CR or LF must appear raw in any logger Warning argument after sanitization");

        // Assert: SwapLabelAsync was not called (gated label early return).
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(It.IsAny<PipelineRun>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "gated label must return early before SwapLabelAsync");
    }

    // ── Success Information: label arg must be the sanitized form ─────────
    // Path: RequestLabelChange → GetRun returns run → neither invalid nor gated → Information log → SwapLabelAsync

    [Fact]
    public async Task RequestLabelChange_ValidLabel_InformationLogArgumentIsSanitizedForm()
    {
        // Arrange: in-memory run present; valid non-gated label reaches the Information log.
        // SanitizeForLog("agent:error") == "agent:error" (no CRLF to escape).
        //
        // TODO (WARNING — TestQualityReviewer / SecurityReviewer): This test passes identically
        // on pre-fix code because SanitizeForLog(AgentLabels.Error) == AgentLabels.Error.
        // No test can fail on pre-fix code for this log site: any CRLF-bearing label fails
        // AgentLabels.All.Contains and is rejected before reaching the Information call.
        // This is a structural impossibility — see class-level remarks. The test anchors
        // the SanitizeForLog wrapping convention; the git grep AC2 check is the regression guard.
        //
        // Implementation note: the Information call has 5 args after the template, exceeding
        // Serilog ILogger's typed generic overloads (max 3). C# resolves to the params object?[]
        // overload. Moq must match that overload via It.Is<object[]>; individual typed matchers
        // would silently target a non-existent 5-param generic overload and never match.
        const string jobId = "job-valid";
        var run = CreateRun(jobId);
        _mockFacade.Setup(f => f.GetRun(new JobId(jobId))).Returns(run);

        var hub = CreateHub();

        // Act
        await hub.RequestLabelChange(jobId, AgentLabels.Error);

        // Assert: Information was emitted with the sanitized label value at args[1].
        _mockLogger.Verify(
            l => l.Information(
                It.Is<string>(t => t.Contains("RequestLabelChange")),
                It.Is<object[]>(args =>
                    args != null &&
                    args.Length >= 2 &&
                    (string?)args[1] == LogSanitizer.SanitizeForLog(AgentLabels.Error))),
            Times.Once,
            "success Information log must pass SanitizeForLog(newLabel) as the label argument");

        // Assert: raw CRLF never appeared in any Information call for this operation.
        // Vacuously true for clean label constants but documents intent and catches future
        // regressions if a CRLF-bearing path is added.
        // TODO (WARNING — TestQualityReviewer): This Times.Never assertion is vacuously true:
        // AgentLabels.All.Contains rejects any CRLF-bearing label before reaching the
        // Information log branch, so no reachable input can trigger it. The assertion
        // documents the absence-of-injection expectation but provides no regression protection.
        _mockLogger.Verify(
            l => l.Information(
                It.IsAny<string>(),
                It.Is<object[]>(args => args != null &&
                    args.Any(a => (a as string) != null &&
                        (((string)a).Contains('\r') || ((string)a).Contains('\n'))))),
            Times.Never,
            "no CR or LF must appear raw in any logger Information argument after sanitization");

        // Assert: SwapLabelAsync was called (happy path proceeds after the log).
        _mockIssueOps.Verify(
            o => o.SwapLabelAsync(run, AgentLabels.Error, It.IsAny<CancellationToken>()),
            Times.Once,
            "valid non-gated label must delegate to SwapLabelAsync");
    }

    // ── AgentId log injection (Issue #3968) ───────────────────────────────
    // Path: RequestLabelChange → GetRun returns run with CRLF in AgentId → Information log
    //
    // Unlike the newLabel tests above, this test is non-tautological: run.AgentId is set on
    // the in-memory PipelineRun and is never validated against AgentLabels.All.Contains().
    // A CRLF-bearing AgentId therefore reaches the Information log call at AgentHub.IssueOps.cs:L97.
    // Pre-fix: args[3] contains raw '\r' and '\n' → assertion fails.
    // Post-fix: args[3] contains '\\r' and '\\n' (escaped) → assertion passes.

    [Fact]
    public async Task RequestLabelChange_AgentIdWithNewline_InformationLogDoesNotContainRawNewline()
    {
        // Arrange: run present with an AgentId that contains CR+LF, simulating a compromised
        // agent that registered with a crafted agentId designed to forge subsequent log lines.
        const string jobId = "job-agentid-injection";
        const string maliciousAgentId = "agent-1\r\n[ERR] forged-log-line";
        const string sanitizedAgentId = "agent-1\\r\\n[ERR] forged-log-line";

        var run = CreateRun(jobId);
        run.AgentId = maliciousAgentId; // set agent-controlled field after construction
        _mockFacade.Setup(f => f.GetRun(new JobId(jobId))).Returns(run);

        var hub = CreateHub();

        // Act: AgentLabels.Error is in AgentLabels.All and not in DispatchGatedLabels,
        // so all guards pass and execution reaches the Information log at line 97.
        await hub.RequestLabelChange(jobId, AgentLabels.Error);

        // Assert: Information was emitted with the *sanitized* AgentId at args[3].
        // Log template: "RequestLabelChange: job {JobId} requesting label {Label} for issue
        //   {IssueIdentifier} (agent={AgentId}, currentStep={CurrentStep})"
        // args[0]=jobId.Value, args[1]=SanitizeForLog(newLabel), args[2]=run.IssueIdentifier,
        // args[3]=run.AgentId (must be sanitized), args[4]=run.CurrentStep
        _mockLogger.Verify(
            l => l.Information(
                It.Is<string>(t => t.Contains("RequestLabelChange")),
                It.Is<object[]>(args =>
                    args != null &&
                    args.Length >= 4 &&
                    (string?)args[3] == sanitizedAgentId)),
            Times.Once,
            "Information log must pass SanitizeForLog(run.AgentId) — raw CRLF in AgentId must not appear in the log");

        // Assert: the raw malicious value (with actual newline chars) must not appear in any arg.
        _mockLogger.Verify(
            l => l.Information(
                It.IsAny<string>(),
                It.Is<object[]>(args => args != null &&
                    args.Any(a => (a as string) != null &&
                        (((string)a).Contains('\r') || ((string)a).Contains('\n'))))),
            Times.Never,
            "no CR or LF must appear raw in any Information log argument — would enable log line injection");
    }

    // TODO: [WARNING — TestQualityReviewer] Issue #3968 also fixed run.AgentId sanitization at 8 additional
    // log call sites (AgentHub.Decomposition.cs:304, AgentIdleTransitioner.cs:63, AgentJobLifecycleService.cs:144,
    // DistributedRunService.cs:132, OrchestratorRunService.cs:85 & 94, RunLifecycleManager.cs:131 & 222 & 283,
    // PipelineRunLifecycleService.cs:254). No per-site regression tests exist for those paths: reverting any
    // SanitizeForLog wrapping there would reintroduce the vulnerability undetected by the test suite.
    // The AC2 git grep check is the primary regression guard for those sites. To add per-site coverage,
    // write tests analogous to RequestLabelChange_AgentIdWithNewline_InformationLogDoesNotContainRawNewline
    // for each method: populate a PipelineRun with a CRLF-bearing AgentId, invoke the method, verify the
    // logger received the escaped form. Priority sites: RunLifecycleManager.FailRunAsync / CompleteRunAsync /
    // CancelRunAsync (all three invoke logger.Information with run.AgentId after terminal state transitions).
}
