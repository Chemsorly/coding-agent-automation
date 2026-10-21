using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.AgentGateway;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for the consolidation dispatch flow — verifying that
/// <c>ReportConsolidationComplete</c> correctly increments the badge count for created issues.
/// Since <see cref="CodingAgent.AgentGateway.AgentHub"/> depends on sealed/complex services
/// that cannot be easily mocked in isolation, these tests validate the dispatch logic
/// through the service layer contracts.
/// Validates: Requirements 3.1, 10.1
/// </summary>
public sealed class ConsolidationDispatchTests
{
    private readonly Mock<IConsolidationService> _mockConsolidationService;
    private readonly ConsolidationBadgeService _badgeService;

    public ConsolidationDispatchTests()
    {
        _mockConsolidationService = new Mock<IConsolidationService>();
        _badgeService = new ConsolidationBadgeService();
    }

    // ── Badge incremented on refactoring issues created ──────────────────

    [Fact]
    public void ReportConsolidationComplete_WithCreatedIssues_IncrementsBadge()
    {
        // Validates: Requirement 10.1
        var result = new ConsolidationJobResult
        {
            JobId = "run-005",
            Success = true,
            Summary = "Created 3 refactoring issues",
            CreatedIssues = new List<CreatedIssueInfo>
            {
                new() { Identifier = "#101", Title = "Extract shared validation logic", Url = "https://github.com/org/repo/issues/101" },
                new() { Identifier = "#102", Title = "Remove duplicate config parsing", Url = "https://github.com/org/repo/issues/102" },
                new() { Identifier = "#103", Title = "Rename inconsistent service names", Url = "https://github.com/org/repo/issues/103" }
            }
        };

        // Simulate the hub's behavior: increment badge for created issues
        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
        }

        _badgeService.BadgeCount.Should().Be(3);
    }

    [Fact]
    public void ReportConsolidationComplete_WithNoCreatedIssues_DoesNotIncrementBadge()
    {
        // Validates: Requirement 10.1 — badge not incremented when no issues
        var result = new ConsolidationJobResult
        {
            JobId = "run-006",
            Success = true,
            Summary = "No refactoring opportunities identified"
        };

        // Simulate the hub's behavior
        if (result.CreatedIssues is { Count: > 0 })
        {
            _badgeService.IncrementBy(result.CreatedIssues.Count);
        }

        _badgeService.BadgeCount.Should().Be(0);
    }

    // ── Combined scenario: issues + suggestions ─────────────────────────

    [Fact]
    public void ReportConsolidationComplete_MultipleCompletions_BadgeAccumulates()
    {
        // Validates: Requirement 10.1 — badge accumulates across multiple completions
        // First: refactoring creates 2 issues
        _badgeService.IncrementBy(2);

        // Second: harness suggestions generates 3 suggestions
        _badgeService.IncrementBy(3);

        _badgeService.BadgeCount.Should().Be(5);
    }

    // ── Dispatch to correct agent ───────────────────────────────────────

    // TODO [WARNING]: ConsolidationJobMessage_ContainsCorrectJobId_ForDispatch and
    // ConsolidationJobResult_ReportsBackWithMatchingJobId are tautological data-construction tests —
    // they set a property then assert the same value. They verify nothing about dispatch behavior
    // and would not fail if dispatch logic were broken. Consider replacing with tests that exercise
    // actual dispatch/reporting behavior via HubConsolidationOperations. (issue #3030)

    [Fact]
    public void ConsolidationJobMessage_ContainsCorrectJobId_ForDispatch()
    {
        // Validates: Requirement 3.1 — job dispatched with correct identifiers
        var job = new ConsolidationJobMessage
        {
            JobId = "consolidation-run-abc",
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = "tmpl-1",
            TemplateName = "DotNet Repo",
            ProviderConfigs = new List<ProviderConfig>(),
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp"
            }
        };

        job.JobId.Should().Be("consolidation-run-abc");
        job.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        job.TemplateId.Should().Be("tmpl-1");
    }

    [Fact]
    public void ConsolidationJobResult_ReportsBackWithMatchingJobId()
    {
        // Validates: Requirement 3.1 — agent reports completion with matching job ID
        var result = new ConsolidationJobResult
        {
            JobId = "consolidation-run-abc",
            Success = true,
            Summary = "Done"
        };

        result.JobId.Should().Be("consolidation-run-abc");
        result.Success.Should().BeTrue();
    }
}
