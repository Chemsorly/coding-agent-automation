using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.IntegrationTests.Helpers;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.IntegrationTests.Pipeline;

/// <summary>
/// Integration tests that wire up real internal services (JsonConfigurationStore,
/// IssueDescriptionParser, SeverityParser, CiLogWriter, PromptBuilder) while
/// mocking only external I/O boundaries (GitHub API, Kiro CLI, GitHub Actions).
/// Catches serialization mismatches, config round-trip bugs, and wiring issues
/// that interface-level mocks hide.
/// </summary>
[Trait("Category", "Integration")]
public class PipelineIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task ConfigRoundTrip_AllFields_PreservesData()
    {
        var original = new PipelineConfiguration
        {
            MaxRetries = 5,
            IssuePageSize = 50,
            AgentTimeout = TimeSpan.FromMinutes(45),
            WorkspaceBaseDirectory = "/tmp/custom-workspaces",
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 3,
                FixPrompt = "Custom fix prompt"
            },
            AnalysisPrompt = "Custom analysis",
            ImplementationPrompt = "Custom implementation",
            ExternalCiTimeout = TimeSpan.FromMinutes(20),
            ExternalCiPollInterval = TimeSpan.FromSeconds(45),
            StallWarningInterval = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromSeconds(15),
            BlacklistedPaths = new[] { ".agent", ".github", ".secret" },
            BlacklistMode = BlacklistMode.Fail,
            FailedWorkspaceRetentionDays = 14,
            LastUsedProviderIds = new Dictionary<string, string>
            {
                ["issue"] = "gh-issue-1",
                ["repository"] = "gh-repo-1",
                ["agent"] = "kiro-1"
            },
            BrainReadOnly = true,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
            ClosedLoopMaxRunsPerCycle = 5,
            ClosedLoopMaxPagesToFetch = 20
        };

        await ConfigStore.SavePipelineConfigAsync(original, CancellationToken.None);

        // Load via a fresh store instance to ensure we're reading from disk
        var freshStore = new JsonConfigurationStore(ConfigDir);
        var loaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);

        loaded.MaxRetries.Should().Be(original.MaxRetries);
        loaded.IssuePageSize.Should().Be(original.IssuePageSize);
        loaded.AgentTimeout.Should().Be(original.AgentTimeout);
        loaded.WorkspaceBaseDirectory.Should().Be(original.WorkspaceBaseDirectory);
        loaded.AnalysisPrompt.Should().Be(original.AnalysisPrompt);
        loaded.ImplementationPrompt.Should().Be(original.ImplementationPrompt);
        loaded.ExternalCiTimeout.Should().Be(original.ExternalCiTimeout);
        loaded.ExternalCiPollInterval.Should().Be(original.ExternalCiPollInterval);
        loaded.StallWarningInterval.Should().Be(original.StallWarningInterval);
        loaded.StallPollInterval.Should().Be(original.StallPollInterval);
        loaded.BlacklistedPaths.Should().BeEquivalentTo(original.BlacklistedPaths);
        loaded.BlacklistMode.Should().Be(original.BlacklistMode);
        loaded.FailedWorkspaceRetentionDays.Should().Be(original.FailedWorkspaceRetentionDays);
        loaded.LastUsedProviderIds.Should().BeEquivalentTo(original.LastUsedProviderIds);
        loaded.BrainReadOnly.Should().Be(original.BrainReadOnly);
        loaded.ClosedLoopPollInterval.Should().Be(original.ClosedLoopPollInterval);
        loaded.ClosedLoopMaxRunsPerCycle.Should().Be(original.ClosedLoopMaxRunsPerCycle);
        loaded.ClosedLoopMaxPagesToFetch.Should().Be(original.ClosedLoopMaxPagesToFetch);

        // Nested CodeReviewConfiguration
        loaded.CodeReview.MaxIterations.Should().Be(3);
        loaded.CodeReview.FixPrompt.Should().Be("Custom fix prompt");
    }

    [Fact]
    public async Task ConfigRoundTrip_NullOptionals_PreservesDefaults()
    {
        var original = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = WorkspaceBase,
            CodeReview = new CodeReviewConfiguration
            {
                FixPrompt = null
            },
            LastUsedProviderIds = new Dictionary<string, string>()
        };

        await ConfigStore.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await new JsonConfigurationStore(ConfigDir).LoadPipelineConfigAsync(CancellationToken.None);

        loaded.CodeReview.FixPrompt.Should().BeNull();
        loaded.LastUsedProviderIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigRoundTrip_TimeSpanEdgeCases_PreservesValues()
    {
        var original = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = WorkspaceBase,
            AgentTimeout = TimeSpan.Zero,
            ExternalCiTimeout = TimeSpan.FromMilliseconds(1),
            ExternalCiPollInterval = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(15),
            StallWarningInterval = TimeSpan.FromDays(1),
            StallPollInterval = TimeSpan.FromSeconds(1),
            ClosedLoopPollInterval = TimeSpan.FromTicks(123456789)
        };

        await ConfigStore.SavePipelineConfigAsync(original, CancellationToken.None);
        var loaded = await new JsonConfigurationStore(ConfigDir).LoadPipelineConfigAsync(CancellationToken.None);

        loaded.AgentTimeout.Should().Be(TimeSpan.Zero);
        loaded.ExternalCiTimeout.Should().Be(TimeSpan.FromMilliseconds(1));
        loaded.ExternalCiPollInterval.Should().Be(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(15));
        loaded.StallWarningInterval.Should().Be(TimeSpan.FromDays(1));
        loaded.StallPollInterval.Should().Be(TimeSpan.FromSeconds(1));
        loaded.ClosedLoopPollInterval.Should().Be(TimeSpan.FromTicks(123456789));
    }

    [Fact]
    public async Task ProviderConfig_RoundTrip_AllKinds()
    {
        var configs = new[]
        {
            new ProviderConfig
            {
                Id = "issue-gh", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.ApiUrl] = "https://api.github.com", [ProviderSettingKeys.Owner] = "org", [ProviderSettingKeys.Repo] = "repo" }
            },
            new ProviderConfig
            {
                Id = "repo-gh", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo",
                RepositoryRole = RepositoryRole.Work,
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.BaseBranch] = "main" }
            },
            new ProviderConfig
            {
                Id = "repo-brain", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain Repo",
                RepositoryRole = RepositoryRole.Brain,
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.BaseBranch] = "main" }
            },
            new ProviderConfig
            {
                Id = "agent-kiro", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent",
                Settings = new Dictionary<string, string> { [ProviderSettingKeys.Model] = "claude-sonnet-4.6" }
            },
            new ProviderConfig
            {
                Id = "pipeline-gh", Kind = ProviderKind.Pipeline, ProviderType = "GitHubActions", DisplayName = "CI"
            }
        };

        foreach (var config in configs)
            await ConfigStore.SaveProviderConfigAsync(config, CancellationToken.None);

        var freshStore = new JsonConfigurationStore(ConfigDir);

        foreach (var original in configs)
        {
            var loaded = await freshStore.LoadProviderConfigsAsync(original.Kind, CancellationToken.None);
            var match = loaded.Should().ContainSingle(c => c.Id == original.Id).Subject;
            match.Kind.Should().Be(original.Kind);
            match.ProviderType.Should().Be(original.ProviderType);
            match.DisplayName.Should().Be(original.DisplayName);
            match.RepositoryRole.Should().Be(original.RepositoryRole);
            foreach (var kvp in original.Settings)
                match.Settings[kvp.Key].Should().Be(kvp.Value);
        }
    }

    [Fact]
    public async Task RunHistoryPersistence_SurvivesServiceRestart()
    {
        await using var service = await CreateServiceWithPersistedConfigAsync();

        var run = await service.StartPipelineAsync(
            "issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        service.GetRunHistory().Should().ContainSingle(s => s.RunId == run.RunId);

        // Simulate restart: create a brand new service pointing at the same runs directory
        await using var service2 = new PipelineOrchestrationService(
            ConfigStore,
            MockFactory.Object,
            new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(MockLogger.Object),
            new QualityGateOrchestrator(MockValidator.Object, new PullRequestOrchestrator(MockLogger.Object), MockLogger.Object),
            MockLogger.Object,
            brainUpdateService: new BrainUpdateService(MockLogger.Object),
            historyService: new PipelineRunHistoryService(MockLogger.Object, RunsDir));

        var history = service2.GetRunHistory();
        var restored = history.Should().ContainSingle(s => s.RunId == run.RunId).Subject;
        restored.IssueIdentifier.Should().Be("42");
        restored.IssueTitle.Should().Be("Test Issue");
        restored.FinalStep.Should().Be(PipelineStep.Completed);
        restored.StartedAt.Should().BeCloseTo(run.StartedAt, TimeSpan.FromSeconds(1));
        restored.CompletedAt.Should().NotBeNull();
        restored.PullRequestUrl.Should().Be("https://github.com/test/pr/1");
        restored.ModelName.Should().Be("test-model");
        restored.InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public async Task FullPipelineFlow_RealInternalServices()
    {
        // Issue with structured markdown so IssueDescriptionParser extracts sections
        MockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "99",
                Title = "Add input validation",
                Description = "## Requirements\nAdd null checks to all public methods\n\n## Acceptance Criteria\n- [ ] All public methods validate inputs\n- [ ] ArgumentNullException thrown for null args",
                Labels = new[] { "enhancement" }
            });

        await using var service = await CreateServiceWithPersistedConfigAsync();

        var steps = new List<PipelineStep>();
        service.OnChange += () =>
        {
            if (service.ActiveRun != null)
                steps.Add(service.ActiveRun.CurrentStep);
        };

        var run = await service.StartPipelineAsync(
            "issue-1", "repo-1", "99", "agent-1", CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Completed);
        run.IssueTitle.Should().Be("Add input validation");

        // Verify IssueDescriptionParser output was wired into PromptBuilder:
        // the agent should have received a prompt containing the issue title and file reference
        MockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Add input validation") && r.Prompt.Contains(PromptBuilder.IssueContextFilePath)),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.AtLeastOnce);

        // Verify expected state transitions occurred
        steps.Should().Contain(PipelineStep.CloningRepository);
        steps.Should().Contain(PipelineStep.CreatingBranch);
        steps.Should().Contain(PipelineStep.GeneratingCode);
        steps.Should().Contain(PipelineStep.Completed);

        // Verify run was persisted to disk
        var runFile = Path.Combine(RunsDir, $"{run.RunId}.json");
        File.Exists(runFile).Should().BeTrue();
    }

    [Fact]
    public async Task CodeReview_RealSeverityParsing()
    {
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = WorkspaceBase,
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 1,
                FixPrompt = PipelineConfiguration.DefaultFixPrompt
            }
        };

        await ConfigStore.SaveReviewerConfigAsync(
            new ReviewerConfiguration { Id = "test-reviewer", DisplayName = "Test", Agents = new[] { new ReviewAgent { Name = "Review", Prompt = "Review the changes." } } },
            CancellationToken.None);

        // Mock agent: analysis + code gen succeed, review writes findings file
        var callCount = 0;
        MockAgentProvider.Setup(p => p.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                var kiroDir = Path.Combine(req.WorkspacePath, ".agent");
                Directory.CreateDirectory(kiroDir);

                if (req.Prompt.Contains("Analyze the codebase"))
                {
                    File.WriteAllText(Path.Combine(kiroDir, "analysis.md"), new string('x', 200));
                    var assessment = new { recommendation = "ready", reason = "Test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() };
                    File.WriteAllText(Path.Combine(kiroDir, "analysis-assessment.json"),
                        System.Text.Json.JsonSerializer.Serialize(assessment, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                }
                else if (req.Prompt.Contains("Review the changes"))
                {
                    // Extract the findings file path from the prompt (per-agent path)
                    var findingsFileName = "review-findings-review.md";
                    File.WriteAllText(Path.Combine(kiroDir, findingsFileName),
                        "1. [CRITICAL] Missing null check in ProcessOrder\n" +
                        "2. [CRITICAL] SQL injection in SearchUsers\n" +
                        "3. [WARNING] Consider using StringBuilder\n" +
                        "4. [SUGGESTION] Rename variable for clarity\n" +
                        "5. [SUGGESTION] Add XML doc comment");
                }
                return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
            });

        await using var service = await CreateServiceWithPersistedConfigAsync(config);

        var run = await service.StartPipelineAsync(
            "issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // SeverityParser should have extracted counts from real findings content
        run.CodeReviewCriticalCount.Should().Be(2);
        run.CodeReviewWarningCount.Should().Be(1);
        run.CodeReviewSuggestionCount.Should().Be(2);

        // Fix prompt should have been sent because criticals exist
        MockAgentProvider.Verify(p => p.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("[CRITICAL]") && r.Prompt.Contains("Fix only") && r.UseResume),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task WorkspaceCleanup_RealFileSystem()
    {
        var expiredRunId = Guid.NewGuid().ToString();
        var recentRunId = Guid.NewGuid().ToString();

        // Create workspace directories
        Directory.CreateDirectory(Path.Combine(WorkspaceBase, expiredRunId));
        Directory.CreateDirectory(Path.Combine(WorkspaceBase, recentRunId));

        // Persist run summaries directly to the runs directory
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        File.WriteAllText(Path.Combine(RunsDir, $"{expiredRunId}.json"),
            System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary
            {
                RunId = expiredRunId, IssueIdentifier = "1", IssueTitle = "Expired run",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTime.UtcNow.AddDays(-30),
                CompletedAt = DateTime.UtcNow.AddDays(-30)
            }, jsonOptions));

        File.WriteAllText(Path.Combine(RunsDir, $"{recentRunId}.json"),
            System.Text.Json.JsonSerializer.Serialize(new PipelineRunSummary
            {
                RunId = recentRunId, IssueIdentifier = "2", IssueTitle = "Recent run",
                FinalStep = PipelineStep.Failed,
                StartedAt = DateTime.UtcNow.AddDays(-1),
                CompletedAt = DateTime.UtcNow.AddDays(-1)
            }, jsonOptions));

        // Create service — its constructor loads run history, and StartPipelineAsync
        // calls CleanupExpiredWorkspaces at the start
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = WorkspaceBase,
            FailedWorkspaceRetentionDays = 7
        };
        await using var service = await CreateServiceWithPersistedConfigAsync(config);

        // Starting a pipeline triggers cleanup
        await service.StartPipelineAsync(
            "issue-1", "repo-1", "42", "agent-1", CancellationToken.None);

        // Expired workspace (30 days old, retention = 7) should be deleted
        Directory.Exists(Path.Combine(WorkspaceBase, expiredRunId)).Should().BeFalse();
        // Recent workspace (1 day old, retention = 7) should be retained
        Directory.Exists(Path.Combine(WorkspaceBase, recentRunId)).Should().BeTrue();
    }
}
