using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ToolUsageDisplay"/> covering ByServer, PhaseSummary and FailureBreakdown.
/// </summary>
public class ToolUsageDisplayTests
{
    // ── ByServer ────────────────────────────────────────────────────────────

    [Fact]
    public void ByServer_SumsCallsAcrossPhases()
    {
        var run = MakeRun(
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 5 },
                }),
                ["codegen"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 3 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Server.Should().Be("sonarqube");
        result[0].Totals.Calls.Should().Be(8);
    }

    [Fact]
    public void ByServer_ConfiguredServerWithNoCallsAppearsWithZero()
    {
        var run = new PipelineRunSummary
        {
            RunId = "r1",
            IssueIdentifier = "1",
            IssueTitle = "t",
            FinalStep = PipelineStep.Completed,
            McpServers = ["sonarqube"],
        };

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Server.Should().Be("sonarqube");
        result[0].Totals.Calls.Should().Be(0);
        result[0].Unavailable.Should().BeNull();
    }

    [Fact]
    public void ByServer_WebAppearsOnlyWhenCalled()
    {
        var runWithWeb = MakeRun(
            mcpServers: ["sonarqube"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["web/search"] = new McpCallCounts { Calls = 2 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(runWithWeb);

        result.Should().HaveCount(2);
        result.Should().Contain(s => s.Server == "web");
        result.Should().Contain(s => s.Server == "sonarqube");

        // Without web calls, "web" does not appear
        var runNoWeb = MakeRun(mcpServers: ["sonarqube"]);
        var resultNoWeb = ToolUsageDisplay.ByServer(runNoWeb);

        resultNoWeb.Should().HaveCount(1);
        resultNoWeb.Should().NotContain(s => s.Server == McpToolKeys.WebServer);
    }

    [Fact]
    public void ByServer_SortedByCallsMostFirst_ThenByNameOrdinal()
    {
        var run = MakeRun(
            mcpServers: ["sonarqube", "grafana", "web"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 10 },
                    ["grafana/query"] = new McpCallCounts { Calls = 20 },
                    ["web/search"] = new McpCallCounts { Calls = 5 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Select(s => s.Server).Should().Equal("grafana", "sonarqube", "web");
    }

    [Fact]
    public void ByServer_SortedByNameWhenCallsAreEqual()
    {
        var run = MakeRun(
            mcpServers: ["zebra", "apple"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["zebra/tool"] = new McpCallCounts { Calls = 3 },
                    ["apple/tool"] = new McpCallCounts { Calls = 3 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run);

        // Same call count → alphabetical (ordinal ignore case)
        result.Select(s => s.Server).Should().Equal("apple", "zebra");
    }

    [Fact]
    public void ByServer_PhaseFilterLimitsToSpecifiedPhases()
    {
        var run = MakeRun(
            phases: new Dictionary<string, PhaseUsage>
            {
                ["triage"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 5 },
                }),
                ["triage_review"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 10 },
                }),
                ["triage_refinement"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 2 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run, ["triage", "triage_refinement"]);

        result.Should().HaveCount(1);
        result[0].Totals.Calls.Should().Be(7, "triage (5) + triage_refinement (2), triage_review excluded");
    }

    [Fact]
    public void ByServer_NeedsAuthWinsOverFailed()
    {
        var run = MakeRun(
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = new PhaseUsage(0, null) with
                {
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["sonarqube"] = McpServerStatuses.Failed,
                    },
                },
                ["codegen"] = new PhaseUsage(0, null) with
                {
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["sonarqube"] = McpServerStatuses.NeedsAuth,
                    },
                },
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Unavailable.Should().Be(McpServerStatuses.NeedsAuth, "needs-auth wins over failed");
    }

    [Fact]
    public void ByServer_EmptyForOldRunWithNullMcpServersAndNoToolCalls()
    {
        var run = new PipelineRunSummary
        {
            RunId = "r1",
            IssueIdentifier = "1",
            IssueTitle = "t",
            FinalStep = PipelineStep.Completed,
            McpServers = null,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = new PhaseUsage(1000, 0.05m),
            },
        };

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ByServer_EmptyForNullRun()
    {
        ToolUsageDisplay.ByServer(null).Should().BeEmpty();
    }

    [Fact]
    public void ByServer_PreservesConfiguredServerCasing()
    {
        var run = MakeRun(
            mcpServers: ["SonarQube"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 7 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Server.Should().Be("SonarQube", "configured casing wins over ToolCalls key casing");
        result[0].Totals.Calls.Should().Be(7);
    }

    [Fact]
    public void ByServer_ToolsSortedByCallsMostFirst()
    {
        var run = MakeRun(
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_project_quality_gate_status"] = new McpCallCounts { Calls = 3 },
                    ["sonarqube/get_file_coverage_details"] = new McpCallCounts { Calls = 17 },
                }),
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Tools.Select(t => t.Tool).Should()
            .Equal("get_file_coverage_details", "get_project_quality_gate_status");
    }

    [Fact]
    public void ByServer_UnavailableServerWithCallsShowsBoth()
    {
        // A server can be unavailable in one phase but still have calls from another
        var run = MakeRun(
            mcpServers: ["sonarqube"],
            phases: new Dictionary<string, PhaseUsage>
            {
                ["analysis"] = MakePhase(new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = new McpCallCounts { Calls = 5 },
                }),
                ["codegen"] = new PhaseUsage(0, null) with
                {
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["sonarqube"] = McpServerStatuses.Failed,
                    },
                },
            });

        var result = ToolUsageDisplay.ByServer(run);

        result.Should().HaveCount(1);
        result[0].Totals.Calls.Should().Be(5);
        result[0].Unavailable.Should().Be(McpServerStatuses.Failed);
    }

    // ── PhaseSummary ────────────────────────────────────────────────────────

    [Fact]
    public void PhaseSummary_FormatsServersByCallsMostFirst()
    {
        var phase = MakePhase(new Dictionary<string, McpCallCounts>
        {
            ["sonarqube/get_issues"] = new McpCallCounts { Calls = 20 },
            ["sonarqube/list_issues"] = new McpCallCounts { Calls = 5 },
            ["web/search"] = new McpCallCounts { Calls = 2 },
        });

        ToolUsageDisplay.PhaseSummary(phase).Should().Be("sonarqube 25 · web 2");
    }

    [Fact]
    public void PhaseSummary_EmptyStringWhenNoToolCalls()
    {
        var phase = new PhaseUsage(1000, 0.05m) with { ToolCalls = null };

        ToolUsageDisplay.PhaseSummary(phase).Should().BeEmpty();
    }

    [Fact]
    public void PhaseSummary_EmptyStringWhenToolCallsEmpty()
    {
        var phase = new PhaseUsage(1000, 0.05m) with
        {
            ToolCalls = new Dictionary<string, McpCallCounts>(),
        };

        ToolUsageDisplay.PhaseSummary(phase).Should().BeEmpty();
    }

    [Fact]
    public void PhaseSummary_NullPhaseReturnsEmpty()
    {
        ToolUsageDisplay.PhaseSummary(null).Should().BeEmpty();
    }

    [Fact]
    public void PhaseSummary_SingleServer()
    {
        var phase = MakePhase(new Dictionary<string, McpCallCounts>
        {
            ["sonarqube/get_issues"] = new McpCallCounts { Calls = 3 },
        });

        ToolUsageDisplay.PhaseSummary(phase).Should().Be("sonarqube 3");
    }

    // ── FailureBreakdown ────────────────────────────────────────────────────

    [Fact]
    public void FailureBreakdown_FormatsAllFourCategories()
    {
        var counts = new McpCallCounts
        {
            Calls = 10,
            FailedConnection = 1,
            FailedAuth = 0,
            FailedOther = 2,
            FailedUnknown = 0,
        };

        ToolUsageDisplay.FailureBreakdown(counts).Should().Be("connection 1, auth 0, other 2, unknown 0");
    }

    [Fact]
    public void FailureBreakdown_AllZerosForSuccessfulCounts()
    {
        var counts = new McpCallCounts { Calls = 5 };

        ToolUsageDisplay.FailureBreakdown(counts).Should().Be("connection 0, auth 0, other 0, unknown 0");
    }

    [Fact]
    public void FailureBreakdown_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ToolUsageDisplay.FailureBreakdown(null!));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PipelineRunSummary MakeRun(
        IReadOnlyList<string>? mcpServers = null,
        IReadOnlyDictionary<string, PhaseUsage>? phases = null)
    {
        return new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "test",
            FinalStep = PipelineStep.Completed,
            McpServers = mcpServers,
            PhaseBreakdown = phases,
        };
    }

    private static PhaseUsage MakePhase(IReadOnlyDictionary<string, McpCallCounts> toolCalls) =>
        new PhaseUsage(0, null) with { ToolCalls = toolCalls };
}
