using CodingAgent.AgentGateway;
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
/// Log-injection regression tests for <see cref="AgentHub.RequestLabelChangeFallbackAsync"/>.
/// A compromised agent could supply a jobId containing CR/LF to forge extra log lines.
/// Every {JobId} log site in RequestLabelChangeFallbackAsync must wrap jobId in SanitizeForLog.
/// </summary>
// TODO (WARNING — DotNetSpecialist / TestQualityReviewer): Only the two early-return paths
// (invalid-label and gated-label) are covered here. The remaining three log sites in
// RequestLabelChangeFallbackAsync are not exercised by any test in this file:
//   1. Metadata-missing warning (~line 151): reached when GetWorkItemIssueMetadataAsync returns null
//      after ResolveIssueProviderForRunAsync succeeds.
//   2. Label-swap "swapping" Information line (~line 159): reached on the happy path through SwapAsync.
//   3. Terminal catch-block warning (~line 191): reached when ResolveIssueProviderForRunAsync or
//      SwapAsync throws any non-cancellation exception.
// These paths require a DB/provider mock setup. Consider adding tests that mock ResolveIssueProviderForRunAsync
// (or the underlying facade methods) to return/throw appropriately, to assert SanitizeForLog is applied
// at those sites too. The catch-block path was explicitly listed in the issue's evidence section.
public sealed class AgentHubFallbackSanitizationTests
{
    private const string RawJobId = "legit-id\nINFO: auth=admin bypassed";
    private const string SanitizedJobId = "legit-id\\nINFO: auth=admin bypassed";

    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub()
    {
        var hub = new AgentHub(new AgentHubDependencies(
            _mockFacade.Object,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            _mockLogger.Object,
            Mock.Of<IAgentOrphanRecoveryService>(),
            HubTestHelpers.CreateNoOpHubContext()));

        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        return hub;
    }

    // ── Invalid-label early-return path ──────────────────────────────────
    // Path: RequestLabelChange → GetRun returns null → RequestLabelChangeFallbackAsync
    //       → AgentLabels.All.Contains(newLabel) == false
    //       → _logger.Warning(template, SanitizeForLog(newLabel), SanitizeForLog(jobId))  [after fix]
    //       → returns early (no DB calls needed)

    [Fact]
    public async Task RequestLabelChange_FallbackInvalidLabel_LogsSanitizedJobId()
    {
        // Arrange: no in-memory run → triggers fallback; invalid label → early return before any DB call
        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();

        // Act
        await hub.RequestLabelChange(new JobId(RawJobId), "not-an-agent-label");

        // Assert: sanitized form was logged
        // Warning<string,string>(template, SanitizeForLog(newLabel), SanitizeForLog(jobId))
        // TODO (WARNING — TestQualityReviewer): The template argument is matched with It.IsAny<string>()
        // and not pinned to the specific warning template. If the argument order or severity were changed,
        // this assertion could silently match a different log call. Consider pinning the template string
        // (e.g. It.Is<string>(t => t.Contains("invalid label"))) to make the assertion more precise.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.IsAny<string>(),   // arg0: SanitizeForLog(newLabel)
                SanitizedJobId),      // arg1: must be the escaped form
            Times.Once);

        // Assert: raw (unescaped) form was NOT logged anywhere
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                RawJobId),            // raw jobId — must never appear
            Times.Never);
    }

    // ── Gated-label early-return path ─────────────────────────────────────
    // Path: RequestLabelChange → GetRun returns null → RequestLabelChangeFallbackAsync
    //       → AgentLabels.DispatchGatedLabels.Contains(newLabel) == true
    //       → _logger.Warning(template, SanitizeForLog(newLabel), SanitizeForLog(jobId))  [after fix]
    //       → returns early

    [Fact]
    public async Task RequestLabelChange_FallbackGatedLabel_LogsSanitizedJobId()
    {
        // Arrange: no in-memory run → triggers fallback; gated label → early return before any DB call
        _mockFacade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns((PipelineRun?)null);

        var hub = CreateHub();

        // Act
        await hub.RequestLabelChange(new JobId(RawJobId), AgentLabels.EpicApproved);

        // Assert: sanitized form was logged
        // TODO (WARNING — TestQualityReviewer): JobId's constructor may normalise/sanitize its value
        // internally; if so, JobId(RawJobId).Value would already have the newline stripped and this
        // test would pass regardless of whether SanitizeForLog is called at the log site (vacuously
        // satisfying the acceptance criterion). Verify that JobId preserves the raw string verbatim
        // to confirm the test genuinely fails before the fix.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.IsAny<string>(),   // arg0: SanitizeForLog(newLabel)
                SanitizedJobId),      // arg1: must be the escaped form
            Times.Once);

        // Assert: raw (unescaped) form was NOT logged
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.IsAny<string>(),
                RawJobId),
            Times.Never);
    }
}
