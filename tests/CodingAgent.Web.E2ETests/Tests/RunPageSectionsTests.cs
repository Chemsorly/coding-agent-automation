using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests proving that every Run page section survives the real path:
/// seeded PipelineRunSummary → API JSON serialization → UiJsonContext source-generated
/// deserialization in the trimmed WebAssembly client → rendered HTML.
/// Each test seeds one run tailored to trigger a specific section and asserts the rendered text.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RunPageSectionsTests : E2ETestBase
{
    public RunPageSectionsTests(E2EFixture fixture) : base(fixture) { }

    private async Task OpenAsync(PipelineRunSummary run)
    {
        await Fixture.HistoryService.AddRunSummaryAsync(run);
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{run.RunId}");
        await Page.WaitForSelectorAsync("[data-testid='run-page'][data-initialized='true']", new() { Timeout = 15_000 });
    }

    // ── Test 1 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TimeoutFailure_ShowsCalloutAndConfigurationDiagnosis()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.Timeout,
            FailureReason = "Agent timed out after 30 minutes",
            LastActiveStep = PipelineStep.GeneratingCode,
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator(".summary-failure-callout[role='alert']"))
            .ToContainTextAsync("Agent timed out after 30 minutes");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-verdict']"))
            .ToHaveTextAsync("Configuration");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-title']"))
            .ToHaveTextAsync("The run hit the agent timeout");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-card']"))
            .ToContainTextAsync("Raise AgentTimeout for the project");
        await Assertions.Expect(Page.Locator(".diagnosis-evidence code"))
            .ToContainTextAsync("Agent timed out after 30 minutes");
    }

    // ── Test 2 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OomFailure_ShowsOutOfMemoryDiagnosis()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.ExitCodeFailure,
            FailureReason = "Container OOMKilled",
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-verdict']"))
            .ToHaveTextAsync("Configuration");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-title']"))
            .ToHaveTextAsync("The agent ran out of memory");
    }

    // ── Test 3 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GateFailingAcrossRuns_ShowsRepositoryDiagnosis()
    {
        var now = DateTimeOffset.UtcNow;
        var gates = new[] { new GateOutcome("Build", false) };

        // Seed oldest first — AddRunSummaryAsync inserts at the front
        var run1 = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9201"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = now.AddHours(-3),
            CompletedAtOffset = now.AddHours(-3).AddMinutes(10),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.QualityGateExhausted,
            QualityGateOutcomes = gates,
        };
        var run2 = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9202"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = now.AddHours(-2),
            CompletedAtOffset = now.AddHours(-2).AddMinutes(10),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.QualityGateExhausted,
            QualityGateOutcomes = gates,
        };
        var run3 = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9203"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.QualityGateExhausted,
            QualityGateOutcomes = gates,
        };

        await Fixture.HistoryService.AddRunSummaryAsync(run1);
        await Fixture.HistoryService.AddRunSummaryAsync(run2);
        await OpenAsync(run3);

        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-verdict']"))
            .ToHaveTextAsync("Repository");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-title']"))
            .ToHaveTextAsync("Quality gate \"Build\" fails across runs");
    }

    // ── Test 4 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GateFailureWithoutOtherRuns_ShowsAgentDiagnosis()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            RepoProviderConfigId = "repo-e2e",
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            FailureCategory = CodingAgent.Pipeline.Models.FailureReason.QualityGateExhausted,
            QualityGateOutcomes = [new GateOutcome("Build", false)],
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-verdict']"))
            .ToHaveTextAsync("Agent");
        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-title']"))
            .ToHaveTextAsync("The agent could not make quality gate \"Build\" pass");
    }

    // ── Test 5 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompletedRun_ShowsNoDiagnosisAndNoCallout()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='diagnosis-card']"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator(".summary-failure-callout"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator("[data-testid='run-usage']"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator("[data-testid='run-tools']"))
            .ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator("[data-testid='pod-log-tail-card']"))
            .ToHaveCountAsync(0);
    }

    // ── Test 6 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_ShowsCostAndBreakdown()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            TotalCost = 1.25m,
            TotalTokens = 12400,
            CacheReadTokens = 2000,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["implementation"] = new PhaseUsage(12400, 1.25m)
                {
                    InputTokens = 10000,
                    OutputTokens = 2000,
                    ReasoningTokens = 400,
                },
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='run-usage'] [data-testid='usage-cost']"))
            .ToHaveTextAsync("$1.25");
        await Assertions.Expect(Page.Locator("[data-testid='run-usage-breakdown']"))
            .ToHaveTextAsync("New tokens 12.4K (input 10.0K · output 2.0K · thinking 400) · Cache read 2.0K");
    }

    // ── Test 7 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UsageOnSubscription_ShowsEstimate()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            TotalCost = 1.25m,
            TotalTokens = 12400,
            CacheReadTokens = 2000,
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["implementation"] = new PhaseUsage(12400, 1.25m)
                {
                    InputTokens = 10000,
                    OutputTokens = 2000,
                    ReasoningTokens = 400,
                    BillingMode = AgentBillingModes.Subscription,
                },
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='run-usage'] [data-testid='usage-cost']"))
            .ToHaveTextAsync("≈ $1.25");
    }

    // ── Test 8 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Agent_ShowsProfileModelAndEffort()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            AgentProfileName = "Profile A",
            ModelName = "model-x",
            AgentEffort = "high",
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='run-agent'] .v"))
            .ToHaveTextAsync("Profile A · model-x · effort high");
    }

    // ── Test 9 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task McpTools_ShowCallsFailuresAndUnavailableServers()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            McpServers = ["sonarqube", "grafana", "idle"],
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["implementation"] = new PhaseUsage(0, null)
                {
                    ToolCalls = new Dictionary<string, McpCallCounts>
                    {
                        ["sonarqube/search"] = new McpCallCounts { Calls = 3, FailedAuth = 1 },
                        ["web/search"] = new McpCallCounts { Calls = 2 },
                    },
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["grafana"] = McpServerStatuses.NeedsAuth,
                    },
                },
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='run-tools-server-sonarqube']"))
            .ToContainTextAsync("3 call(s)");
        await Assertions.Expect(Page.Locator("[data-testid='run-tools-server-sonarqube']"))
            .ToContainTextAsync("1 failed");
        await Assertions.Expect(
                Page.Locator("[data-testid='run-tools-server-sonarqube'] span[title]"))
            .ToHaveAttributeAsync("title", "connection 0, auth 1, other 0, unknown 0");
        await Assertions.Expect(Page.Locator("[data-testid='run-tools-server-web']"))
            .ToContainTextAsync("2 call(s)");
        await Assertions.Expect(Page.Locator("[data-testid='run-tools-unavailable-grafana']"))
            .ToHaveTextAsync("unavailable (needs-auth)");
        await Assertions.Expect(Page.Locator("[data-testid='run-tools-server-idle']"))
            .ToContainTextAsync("not called");
        await Assertions.Expect(Page.Locator("[data-testid='run-tools']"))
            .ToContainTextAsync("search ×3");
    }

    // ── Test 10 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PhaseBreakdown_ExpandsToPhasesOrderedByCost()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            McpServers = ["sonarqube", "grafana", "idle"],
            PhaseBreakdown = new Dictionary<string, PhaseUsage>
            {
                ["implementation"] = new PhaseUsage(5000, 0.50m)
                {
                    ToolCalls = new Dictionary<string, McpCallCounts>
                    {
                        ["sonarqube/search"] = new McpCallCounts { Calls = 3, FailedAuth = 1 },
                        ["web/search"] = new McpCallCounts { Calls = 2 },
                    },
                    UnavailableMcpServers = new Dictionary<string, string>
                    {
                        ["grafana"] = McpServerStatuses.NeedsAuth,
                    },
                },
                ["review_Correctness"] = new PhaseUsage(9000, 1.00m),
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator(".phase-breakdown-header"))
            .ToContainTextAsync("(2 phases)");
        await Page.Locator(".phase-breakdown-header").ClickAsync();

        var rows = Page.Locator(".phase-breakdown tbody tr");
        await Assertions.Expect(rows).ToHaveCountAsync(2);

        var row1 = rows.Nth(0);
        await Assertions.Expect(row1).ToContainTextAsync("Review: Correctness");
        await Assertions.Expect(row1).ToContainTextAsync("$1.00");
        await Assertions.Expect(row1).ToContainTextAsync("—");

        var row2 = rows.Nth(1);
        await Assertions.Expect(row2).ToContainTextAsync("Implementation");
        await Assertions.Expect(row2).ToContainTextAsync("$0.50");
        await Assertions.Expect(row2).ToContainTextAsync("sonarqube 3 · web 2");
    }

    // ── Test 11 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewRun_ShowsFindingsAndReviewers()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("77"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Review,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            CodeReviewCriticalCount = 2,
            CodeReviewWarningCount = 3,
            CodeReviewSuggestionCount = 4,
            CodeReviewAgentsRun = ["Correctness", "Security"],
            ReviewPrUrl = "https://example.test/pr/77",
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator(".cockpit-finding.is-critical .cockpit-finding-v"))
            .ToHaveTextAsync("2");
        await Assertions.Expect(Page.Locator(".cockpit-finding.is-warning .cockpit-finding-v"))
            .ToHaveTextAsync("3");
        await Assertions.Expect(Page.Locator(".cockpit-finding.is-suggestion .cockpit-finding-v"))
            .ToHaveTextAsync("4");
        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("Reviewers: Correctness, Security");
        await Assertions.Expect(Page.Locator("h1"))
            .ToContainTextAsync("PR #77");
        await Assertions.Expect(Page.Locator("a.cockpit-link-chip:has-text('PR under review')"))
            .ToHaveAttributeAsync("href", "https://example.test/pr/77");
    }

    // ── Test 12 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefactoringScan_ShowsSettingsCreatedDroppedAndUnfiled()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Consolidation,
            ConsolidationType = ConsolidationRunType.RefactoringDetection,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            RefactoringScanOutcome = new RefactoringScanOutcome
            {
                ProposalCount = 3,
                MaxIssues = 5,
                HotspotLookbackDays = 30,
                ReviewEnabled = true,
                AutoDispatch = false,
                AnalyzedCommitSha = "abcdef1234567890",
                CreatedIssues =
                [
                    new CreatedIssueInfo
                    {
                        Identifier = "501",
                        Title = "Split the service",
                        Url = "https://example.test/501",
                        Category = "structure",
                    },
                ],
                DroppedProposals =
                [
                    new RefactoringProposalNote { Title = "Dropped one", Reason = "No evidence" },
                ],
                UnfiledProposals =
                [
                    new RefactoringProposalNote { Title = "Unfiled one" },
                ],
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='scan-settings']"))
            .ToContainTextAsync("Max issues 5");
        await Assertions.Expect(Page.Locator("[data-testid='scan-settings']"))
            .ToContainTextAsync("Hotspot lookback 30 days");
        await Assertions.Expect(Page.Locator("[data-testid='scan-settings']"))
            .ToContainTextAsync("Adversarial review On");
        await Assertions.Expect(Page.Locator("[data-testid='scan-settings']"))
            .ToContainTextAsync("Auto-dispatch Off");
        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("1 of 3 proposals");
        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("abcdef123456");
        await Assertions.Expect(Page.Locator("[data-testid='scan-created-issues']"))
            .ToContainTextAsync("#501");
        await Assertions.Expect(Page.Locator("[data-testid='scan-created-issues']"))
            .ToContainTextAsync("Split the service");
        await Assertions.Expect(Page.Locator("[data-testid='scan-created-issues']"))
            .ToContainTextAsync("structure");
        await Assertions.Expect(Page.Locator("[data-testid='scan-dropped-proposals']"))
            .ToContainTextAsync("Dropped one");
        await Assertions.Expect(Page.Locator("[data-testid='scan-dropped-proposals']"))
            .ToContainTextAsync("No evidence");
        await Assertions.Expect(Page.Locator("[data-testid='scan-unfiled-proposals']"))
            .ToContainTextAsync("Unfiled one");
        await Assertions.Expect(Page.Locator("[data-testid='scan-unfiled-proposals']"))
            .ToContainTextAsync("These proposals exist only in this run.");
        await Assertions.Expect(Page.Locator("[data-testid='consolidation-result']"))
            .ToHaveCountAsync(0);
    }

    // ── Test 13 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BrainConsolidation_AdminSeesCountsLinesAndCommitLink()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Consolidation,
            ConsolidationType = ConsolidationRunType.BrainConsolidation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            BrainConsolidationOutcome = new BrainConsolidationOutcome
            {
                Promoted = 2,
                Appended = 1,
                Lines =
                [
                    new BrainReviewLine
                    {
                        Action = "promoted",
                        Scope = "general",
                        File = "general/SKILL.md",
                        Text = "a lesson",
                    },
                ],
                PushedCommitSha = "abc123def",
                PushedCommitUrl = "https://example.test/commit/abc123def",
            },
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='brain-review-counts']"))
            .ToContainTextAsync("Promoted");
        await Assertions.Expect(Page.Locator("[data-testid='brain-review-counts']"))
            .ToContainTextAsync("2");
        await Assertions.Expect(Page.Locator("[data-testid='brain-review-lines']"))
            .ToContainTextAsync("general/SKILL.md");
        await Assertions.Expect(Page.Locator("[data-testid='brain-review-lines']"))
            .ToContainTextAsync("a lesson");
        await Assertions.Expect(Page.Locator("[data-testid='brain-commit-link']"))
            .ToContainTextAsync("abc123d");
        await Assertions.Expect(Page.Locator("[data-testid='brain-commit-link']"))
            .ToHaveAttributeAsync("href", "https://example.test/commit/abc123def");
    }

    // ── Test 14 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImplementationRunWithBrain_ShowsSkillsAndInboxLink()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            BrainRepoUsed = true,
            BrainSkillsResult = "injected",
            BrainSkillsBytes = 2048,
            BrainSkillsCommit = "1234567abc",
            BrainUpdatesPushed = true,
            BrainInboxFile = "inbox/run.md",
            BrainInboxFileUrl = "https://example.test/inbox",
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("Skills injected, 2 KB · inbox pushed");
        await Assertions.Expect(Page.Locator("body"))
            .ToContainTextAsync("1234567");
        await Assertions.Expect(Page.Locator("a:has([data-testid='brain-inbox-file'])"))
            .ToHaveAttributeAsync("href", "https://example.test/inbox");
        await Assertions.Expect(Page.Locator("[data-testid='brain-inbox-file']"))
            .ToHaveTextAsync("inbox/run.md");
    }

    // ── Test 15 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PodLog_ShowsLastLines()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
            PodLogTail = ["first line", "last line"],
        };
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='pod-log-tail-card']"))
            .ToContainTextAsync("(last 2 lines)");
        await Assertions.Expect(Page.Locator("[data-testid='pod-log-tail-card']"))
            .ToContainTextAsync("first line");
        await Assertions.Expect(Page.Locator("[data-testid='pod-log-tail-card']"))
            .ToContainTextAsync("last line");
    }

    // ── Test 16 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriageRun_LinksToItsTriage()
    {
        var now = DateTimeOffset.UtcNow;
        var triageId = Guid.NewGuid();
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier("9200"),
            IssueTitle = "Sections run",
            RunType = PipelineRunType.Triage,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
        };

        // Register the route intercept before navigating so it is in place
        // when the page makes the triage lookup after loading.
        await Page.RouteAsync($"**/api/ui/triages/by-run/{run.RunId}", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = $"{{\"id\":\"{triageId}\"}}",
        }));
        await OpenAsync(run);

        await Assertions.Expect(Page.Locator("[data-testid='run-triage-link']"))
            .ToHaveAttributeAsync("href", $"triage/{triageId}");
    }
}
