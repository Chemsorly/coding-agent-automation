using AwesomeAssertions;
using MessagePack;
using MessagePack.Resolvers;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Round-trip serialization tests for all SignalR hub message types.
/// Uses ContractlessStandardResolverAllowPrivate to match production SignalR config
/// and catch property renames/refactoring that would cause silent data loss.
/// **Validates: Requirements 2.3**
/// </summary>
public class HubMessageSerializationTests
{
    /// <summary>
    /// Production-equivalent serializer options. ContractlessStandardResolverAllowPrivate
    /// handles both attributed ([MessagePackObject]/[Key]) and unannotated types,
    /// including private setters — the most permissive resolver that mirrors runtime behavior.
    /// </summary>
    protected static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    /// <summary>
    /// Helper: serialize → deserialize round-trip using production options.
    /// </summary>
    protected static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    /// <summary>
    /// Smoke test: verify the resolver can handle a trivial attributed message.
    /// Ensures test infrastructure is correctly wired before adding per-type tests.
    /// </summary>
    [Fact]
    public void Resolver_CanSerializeAttributedMessage()
    {
        var original = new HeartbeatMessage
        {
            AgentId = "smoke-test-agent",
            Timestamp = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.AnalyzingCode,
            MemoryUsageMb = 2048
        };

        var deserialized = RoundTrip(original);

        deserialized.AgentId.Should().Be(original.AgentId);
        deserialized.Timestamp.Should().Be(original.Timestamp);
        deserialized.CurrentStep.Should().Be(original.CurrentStep);
        deserialized.MemoryUsageMb.Should().Be(original.MemoryUsageMb);
    }

    /// <summary>
    /// Round-trip test for JobCompletionPayload with ALL properties populated.
    /// Includes nested RunFeedback (HarnessFeedback + IssueFeedback) and a separate
    /// QualityGateReport round-trip since both are transmitted over SignalR.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Fact]
    public void JobCompletionPayload_RoundTrip_AllPropertiesPopulated()
    {
        var completedAt = new DateTimeOffset(2026, 6, 10, 14, 30, 0, TimeSpan.FromHours(2));
        var original = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            FailureReason = "Quality gate threshold not met",
            PullRequestUrl = "https://github.com/org/repo/pull/42",
            PullRequestNumber = "42",
            IsDraftPr = true,
            RetryCount = 3,
            CompletedAt = completedAt,
            FilesChangedCount = 15,
            LinesAdded = 450,
            LinesRemoved = 120,
            BrainUpdatesPushed = true,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            IsRework = true,
            AnalysisConcerns = new[] { "Thread safety concern", "Missing null check" },
            AnalysisBlockingIssues = new[] { "Dependency #99 not merged" },
            BlacklistedFilesDetected = new[] { ".env.production", "secrets/keys.json" },
            CodeReviewAgentsRun = new[] { "Correctness", "Security", "Performance" },
            CodeReviewCriticalCount = 1,
            CodeReviewWarningCount = 4,
            CodeReviewSuggestionCount = 7,
            Feedback = new RunFeedback
            {
                Outcome = FeedbackOutcome.Success,
                CollectedAtUtc = new DateTime(2026, 6, 10, 12, 30, 0, DateTimeKind.Utc),
                Harness = new HarnessFeedback
                {
                    Category = "missing file context",
                    StuckReason = "Could not find test fixtures",
                    MissingContext = new[] { "test/fixtures/sample.json", "docs/api-spec.yaml" },
                    MissingCapabilities = new[] { "database migration runner" },
                    PromptIssues = new[] { "Contradictory instructions about test coverage" },
                    Suggestions = new[] { "Include fixture files in workspace", "Add DB schema to context" }
                },
                Issue = new IssueFeedback
                {
                    Category = "contradictory acceptance criteria",
                    Description = "AC #2 conflicts with AC #4 regarding error handling",
                    AffectedFiles = new[] { "src/Controllers/UserController.cs", "src/Services/AuthService.cs" },
                    HumanActionNeeded = "Clarify whether 401 or 403 is expected for expired tokens"
                }
            },
            TotalTokens = 125000,
            TotalCost = 2.47m,
            FinalLabel = "agent:done"
        };

        var deserialized = RoundTrip(original);

        // Scalar properties
        deserialized.FinalStep.Should().Be(PipelineStep.Completed);
        deserialized.FailureReason.Should().Be("Quality gate threshold not met");
        deserialized.PullRequestUrl.Should().Be("https://github.com/org/repo/pull/42");
        deserialized.PullRequestNumber.Should().Be("42");
        deserialized.IsDraftPr.Should().BeTrue();
        deserialized.RetryCount.Should().Be(3);
        deserialized.CompletedAt.Should().Be(completedAt);
        deserialized.FilesChangedCount.Should().Be(15);
        deserialized.LinesAdded.Should().Be(450);
        deserialized.LinesRemoved.Should().Be(120);
        deserialized.BrainUpdatesPushed.Should().BeTrue();
        deserialized.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        deserialized.IsRework.Should().BeTrue();
        deserialized.TotalTokens.Should().Be(125000);
        deserialized.TotalCost.Should().Be(2.47m);
        deserialized.FinalLabel.Should().Be("agent:done");

        // Collection properties
        deserialized.AnalysisConcerns.Should().BeEquivalentTo(new[] { "Thread safety concern", "Missing null check" });
        deserialized.AnalysisBlockingIssues.Should().BeEquivalentTo(new[] { "Dependency #99 not merged" });
        deserialized.BlacklistedFilesDetected.Should().BeEquivalentTo(new[] { ".env.production", "secrets/keys.json" });
        deserialized.CodeReviewAgentsRun.Should().BeEquivalentTo(new[] { "Correctness", "Security", "Performance" });
        deserialized.CodeReviewCriticalCount.Should().Be(1);
        deserialized.CodeReviewWarningCount.Should().Be(4);
        deserialized.CodeReviewSuggestionCount.Should().Be(7);

        // RunFeedback nested object
        deserialized.Feedback.Should().NotBeNull();
        deserialized.Feedback!.Outcome.Should().Be(FeedbackOutcome.Success);
        deserialized.Feedback.CollectedAtUtc.Should().Be(new DateTime(2026, 6, 10, 12, 30, 0, DateTimeKind.Utc));

        // HarnessFeedback
        deserialized.Feedback.Harness.Category.Should().Be("missing file context");
        deserialized.Feedback.Harness.StuckReason.Should().Be("Could not find test fixtures");
        deserialized.Feedback.Harness.MissingContext.Should().BeEquivalentTo(new[] { "test/fixtures/sample.json", "docs/api-spec.yaml" });
        deserialized.Feedback.Harness.MissingCapabilities.Should().BeEquivalentTo(new[] { "database migration runner" });
        deserialized.Feedback.Harness.PromptIssues.Should().BeEquivalentTo(new[] { "Contradictory instructions about test coverage" });
        deserialized.Feedback.Harness.Suggestions.Should().BeEquivalentTo(new[] { "Include fixture files in workspace", "Add DB schema to context" });

        // IssueFeedback
        deserialized.Feedback.Issue.Should().NotBeNull();
        deserialized.Feedback.Issue!.Category.Should().Be("contradictory acceptance criteria");
        deserialized.Feedback.Issue.Description.Should().Be("AC #2 conflicts with AC #4 regarding error handling");
        deserialized.Feedback.Issue.AffectedFiles.Should().BeEquivalentTo(new[] { "src/Controllers/UserController.cs", "src/Services/AuthService.cs" });
        deserialized.Feedback.Issue.HumanActionNeeded.Should().Be("Clarify whether 401 or 403 is expected for expired tokens");
    }

    /// <summary>
    /// Round-trip test for QualityGateReport with all nested objects populated.
    /// QualityGateReport is transmitted via SignalR (ReportQualityGateResult hub method)
    /// and must survive MessagePack serialization intact.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Fact]
    public void QualityGateReport_RoundTrip_AllPropertiesPopulated()
    {
        var timestamp = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var original = new QualityGateReport
        {
            Compilation = new GateResult
            {
                GateName = "Compilation",
                Passed = true,
                Details = "Build succeeded in 45s",
                TestsPassed = null,
                TestsFailed = null,
                TestsSkipped = null,
                TestsQuarantined = null,
                QuarantinedTestNames = null,
                CoveragePercent = null
            },
            Tests = new GateResult
            {
                GateName = "Tests",
                Passed = true,
                Details = "142 tests passed, 2 quarantined",
                TestsPassed = 142,
                TestsFailed = 0,
                TestsSkipped = 3,
                TestsQuarantined = 2,
                QuarantinedTestNames = new[] { "FlakyNetworkTest", "TimingDependentTest" },
                CoveragePercent = 87.5
            },
            Coverage = new GateResult
            {
                GateName = "Coverage",
                Passed = true,
                Details = "87.5% coverage (threshold: 80%)",
                CoveragePercent = 87.5
            },
            SecurityScan = new GateResult
            {
                GateName = "SecurityScan",
                Passed = false,
                Details = "1 high-severity vulnerability in dependency"
            },
            ExternalCi = new GateResult
            {
                GateName = "ExternalCI",
                Passed = true,
                Details = "GitHub Actions workflow completed"
            },
            QgcResults = new List<QgcExecutionResult>
            {
                new()
                {
                    QgcId = "qgc-build-test",
                    DisplayName = "Build & Test",
                    Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                    Tests = new GateResult
                    {
                        GateName = "Tests",
                        Passed = true,
                        Details = "All passed",
                        TestsPassed = 100,
                        TestsFailed = 0,
                        TestsSkipped = 1,
                        TestsQuarantined = 0
                    },
                    Coverage = new GateResult { GateName = "Coverage", Passed = true, CoveragePercent = 92.0 }
                },
                new()
                {
                    QgcId = "qgc-security",
                    DisplayName = "Security Scan",
                    Compilation = null,
                    Tests = null,
                    Coverage = null,
                    SecurityScan = new GateResult { GateName = "SecurityScan", Passed = false, Details = "CVE-2026-1234" }
                }
            },
            Timestamp = timestamp
        };

        var deserialized = RoundTrip(original);

        // Compilation
        deserialized.Compilation.GateName.Should().Be("Compilation");
        deserialized.Compilation.Passed.Should().BeTrue();
        deserialized.Compilation.Details.Should().Be("Build succeeded in 45s");

        // Tests
        deserialized.Tests.GateName.Should().Be("Tests");
        deserialized.Tests.Passed.Should().BeTrue();
        deserialized.Tests.Details.Should().Be("142 tests passed, 2 quarantined");
        deserialized.Tests.TestsPassed.Should().Be(142);
        deserialized.Tests.TestsFailed.Should().Be(0);
        deserialized.Tests.TestsSkipped.Should().Be(3);
        deserialized.Tests.TestsQuarantined.Should().Be(2);
        deserialized.Tests.QuarantinedTestNames.Should().BeEquivalentTo(new[] { "FlakyNetworkTest", "TimingDependentTest" });
        deserialized.Tests.CoveragePercent.Should().Be(87.5);

        // Coverage (optional, populated)
        deserialized.Coverage.Should().NotBeNull();
        deserialized.Coverage!.GateName.Should().Be("Coverage");
        deserialized.Coverage.Passed.Should().BeTrue();
        deserialized.Coverage.CoveragePercent.Should().Be(87.5);

        // SecurityScan (optional, populated)
        deserialized.SecurityScan.Should().NotBeNull();
        deserialized.SecurityScan!.GateName.Should().Be("SecurityScan");
        deserialized.SecurityScan.Passed.Should().BeFalse();
        deserialized.SecurityScan.Details.Should().Be("1 high-severity vulnerability in dependency");

        // ExternalCi (optional, populated)
        deserialized.ExternalCi.Should().NotBeNull();
        deserialized.ExternalCi!.GateName.Should().Be("ExternalCI");
        deserialized.ExternalCi.Passed.Should().BeTrue();

        // QgcResults
        deserialized.QgcResults.Should().HaveCount(2);
        var qgc1 = deserialized.QgcResults[0];
        qgc1.QgcId.Should().Be("qgc-build-test");
        qgc1.DisplayName.Should().Be("Build & Test");
        qgc1.Compilation.Should().NotBeNull();
        qgc1.Compilation!.Passed.Should().BeTrue();
        qgc1.Tests.Should().NotBeNull();
        qgc1.Tests!.TestsPassed.Should().Be(100);
        qgc1.Coverage.Should().NotBeNull();
        qgc1.Coverage!.CoveragePercent.Should().Be(92.0);

        var qgc2 = deserialized.QgcResults[1];
        qgc2.QgcId.Should().Be("qgc-security");
        qgc2.DisplayName.Should().Be("Security Scan");
        qgc2.Compilation.Should().BeNull();
        qgc2.Tests.Should().BeNull();
        qgc2.Coverage.Should().BeNull();
        qgc2.SecurityScan.Should().NotBeNull();
        qgc2.SecurityScan!.Passed.Should().BeFalse();
        qgc2.SecurityScan.Details.Should().Be("CVE-2026-1234");

        // Timestamp
        deserialized.Timestamp.Should().Be(timestamp);

        // Computed property (not serialized but should be consistent)
        deserialized.AllPassed.Should().BeFalse(); // SecurityScan failed
    }

    /// <summary>
    /// Round-trip test for JobAssignmentMessage with ALL properties populated.
    /// Catches silent data loss from property renames, type mismatches, or missing
    /// MessagePack annotations on nested types.
    /// **Validates: Requirements 2.1, 2.4**
    /// </summary>
    [Fact]
    public void JobAssignmentMessage_RoundTrip_AllPropertiesPopulated()
    {
        var original = new JobAssignmentMessage
        {
            JobId = "job-abc-123",
            IssueIdentifier = "org/repo#42",
            IssueDetail = new IssueDetail
            {
                Identifier = "org/repo#42",
                Title = "Implement widget factory",
                Description = "Full description with **markdown** and `code`",
                Labels = new[] { "agent:next", "priority:high", "area:backend" }
            },
            ParsedIssue = new ParsedIssue
            {
                RequirementsSection = "## Requirements\n- Must handle null inputs\n- Must be thread-safe",
                AcceptanceCriteria = new[] { "Widget is created", "Factory is singleton", "Thread-safe access" }
            },
            IssueComments = new List<IssueComment>
            {
                new()
                {
                    Id = "comment-1",
                    Body = "First comment with context",
                    Author = "reviewer-one",
                    CreatedAt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "comment-2",
                    Body = "Follow-up clarification",
                    Author = "author-two",
                    CreatedAt = new DateTime(2025, 6, 16, 14, 0, 0, DateTimeKind.Utc)
                }
            },
            ExistingAnalysis = "Previous analysis content from prior run",
            ForceRefreshAnalysis = true,
            LinkedPullRequest = new LinkedPullRequest
            {
                Number = 99,
                BranchName = "feature/widget-factory",
                Url = "https://github.com/org/repo/pull/99",
                IsDraft = true,
                IsMergeable = false,
                ReviewComments = new List<PullRequestReviewComment>
                {
                    new()
                    {
                        Id = "rc-1",
                        Body = "Consider using a lock here",
                        Author = "senior-dev",
                        CreatedAt = new DateTime(2025, 6, 17, 9, 0, 0, DateTimeKind.Utc),
                        Path = "src/WidgetFactory.cs"
                    }
                }
            },
            RepoProviderConfigId = "repo-provider-abc",
            AgentProviderConfigId = "agent-provider-xyz",
            BrainProviderConfigId = "brain-provider-def",
            PipelineProviderConfigId = "pipeline-provider-ghi",
            ProviderConfigs = new List<ProviderConfig>
            {
                new()
                {
                    Id = "pc-repo-1",
                    Kind = ProviderKind.Repository,
                    ProviderType = "GitHub",
                    DisplayName = "Main Work Repo",
                    Settings = new Dictionary<string, string>
                    {
                        ["token"] = "ghp_short_lived_token",
                        ["owner"] = "org",
                        ["repo"] = "repo"
                    },
                    RepositoryRole = RepositoryRole.Work,
                    RequiredLabels = new[] { "kiro", "dotnet" },
                    BlacklistedPaths = new[] { "docs/", ".github/" },
                    BlacklistMode = BlacklistMode.WarnAndExclude,
                    Secrets = new Dictionary<string, string> { ["NPM_TOKEN"] = "secret-val" },
                    SetupSteps = new List<SetupStep>
                    {
                        new() { Name = "Install deps", Command = "dotnet restore" }
                    },
                    SteeringContent = "# Repo steering\nFollow TDD."
                },
                new()
                {
                    Id = "pc-agent-1",
                    Kind = ProviderKind.Agent,
                    ProviderType = "KiroCli",
                    DisplayName = "Kiro Agent",
                    Settings = new Dictionary<string, string> { ["model"] = "claude-sonnet" }
                }
            },
            PipelineConfiguration = new PipelineConfiguration
            {
                MaxRetries = 3,
                MaxAnalysisRetries = 2,
                IssuePageSize = 50,
                AgentTimeout = TimeSpan.FromMinutes(45),
                WorkspaceBaseDirectory = "/workspaces/agent",
                AnalysisReviewEnabled = true,
                BaselineHealthCheckEnabled = false,
                ExternalCiTimeout = TimeSpan.FromMinutes(10),
                ExternalCiPollInterval = TimeSpan.FromSeconds(30),
                MaxRefactoringProposals = 5,
                MaxDecompositionSubIssues = 8,
                MaxConcurrentDecompositions = 3,
                DecompositionTimeout = TimeSpan.FromMinutes(20),
                MaxOpenIssuesForContext = 25,
                BrainReadOnly = true
            },
            InitiatedBy = "pipeline-orchestrator",
            ResolvedProfileId = "profile-dotnet-10",
            QualityGateConfigs = new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qg-1",
                    DisplayName = "Build & Test",
                    MatchLabels = new[] { "dotnet" },
                    CompilationCommand = "dotnet",
                    CompilationArguments = new[] { "build", "--no-restore" },
                    TestCommand = "dotnet",
                    TestArguments = new[] { "test", "--no-build" },
                    CoverageThreshold = 80.0,
                    Enabled = true,
                    ExecutionOrder = 1,
                    CoverageReportFormat = "cobertura",
                    CoverageReportPaths = new[] { "TestResults/**/coverage.cobertura.xml" },
                    TestQuarantine = new TestQuarantineConfiguration
                    {
                        Enabled = true,
                        QuarantinedTests = new[]
                        {
                            new QuarantinedTest
                            {
                                TestName = "FlakyIntegrationTest",
                                Reason = "Intermittent network timeout",
                                QuarantinedAt = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                                ExpiresAt = new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                                AssociatedSourceFiles = new[] { "src/Integration/HttpClient.cs" }
                            }
                        },
                        MaxQuarantinedFailuresPerRun = 3
                    }
                }
            },
            McpServers = new List<McpServerConfig>
            {
                new()
                {
                    Name = "context7",
                    Type = "stdio",
                    Command = "npx",
                    Args = new[] { "-y", "@context7/mcp" },
                    Url = null,
                    Env = new Dictionary<string, string> { ["NODE_ENV"] = "production" },
                    Disabled = false,
                    AutoApprove = new[] { "resolve-library-id" }
                }
            },
            ReviewerConfigs = new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rev-cfg-1",
                    DisplayName = "Security Review",
                    MatchLabels = new[] { "security" },
                    Agents = new[]
                    {
                        new ReviewAgent { Name = "SecurityReviewer", Prompt = "Review for vulnerabilities" }
                    },
                    Enabled = true,
                    ExecutionOrder = 2
                }
            },
            LinkedIssueContexts = new List<LinkedIssueContext>
            {
                new()
                {
                    Identifier = "org/repo#40",
                    Title = "Parent feature issue",
                    Description = "Original feature request description"
                }
            },
            RunType = PipelineRunType.Review,
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = "Implements widget factory with thread safety",
            ProjectContext = new DecompositionProjectContext
            {
                ProjectName = "Widget Platform",
                Repositories = new[]
                {
                    new RepositoryTarget
                    {
                        TemplateName = "backend-api",
                        Description = "Core API service",
                        DecompositionEnabled = true,
                        Available = true,
                        Labels = new[] { "dotnet", "api" },
                        IssueProviderId = "ip-backend",
                        RepoProviderId = "rp-backend",
                        LocalPath = "repos/backend-api"
                    }
                }
            },
            ProjectId = "proj-widget-001",
            ProjectName = "Widget Platform",
            ProjectSecrets = new Dictionary<string, string>
            {
                ["SHARED_API_KEY"] = "proj-secret-value",
                ["DB_CONNECTION"] = "Server=localhost;Database=widgets"
            },
            ReviewPrAuthor = "contributor-user",
            ProjectSteeringContent = "# Project Steering\nAll repos must use semantic versioning.",
            RepoSteeringContent = "# Repo Steering\nFollow conventional commits.",
            TraceContext = new Dictionary<string, string>
            {
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                ["tracestate"] = "congo=t61rcWkgMzE"
            }
        };

        var deserialized = RoundTrip(original);

        // Top-level scalar properties
        deserialized.JobId.Should().Be(original.JobId);
        deserialized.IssueIdentifier.Should().Be(original.IssueIdentifier);
        deserialized.ExistingAnalysis.Should().Be(original.ExistingAnalysis);
        deserialized.ForceRefreshAnalysis.Should().Be(original.ForceRefreshAnalysis);
        deserialized.RepoProviderConfigId.Should().Be(original.RepoProviderConfigId);
        deserialized.AgentProviderConfigId.Should().Be(original.AgentProviderConfigId);
        deserialized.BrainProviderConfigId.Should().Be(original.BrainProviderConfigId);
        deserialized.PipelineProviderConfigId.Should().Be(original.PipelineProviderConfigId);
        deserialized.InitiatedBy.Should().Be(original.InitiatedBy);
        deserialized.ResolvedProfileId.Should().Be(original.ResolvedProfileId);
        deserialized.RunType.Should().Be(original.RunType);
        deserialized.ReviewPrTargetBranch.Should().Be(original.ReviewPrTargetBranch);
        deserialized.ReviewPrDescription.Should().Be(original.ReviewPrDescription);
        deserialized.ProjectId.Should().Be(original.ProjectId);
        deserialized.ProjectName.Should().Be(original.ProjectName);
        deserialized.ReviewPrAuthor.Should().Be(original.ReviewPrAuthor);
        deserialized.ProjectSteeringContent.Should().Be(original.ProjectSteeringContent);
        deserialized.RepoSteeringContent.Should().Be(original.RepoSteeringContent);

        // IssueDetail
        deserialized.IssueDetail.Identifier.Should().Be(original.IssueDetail.Identifier);
        deserialized.IssueDetail.Title.Should().Be(original.IssueDetail.Title);
        deserialized.IssueDetail.Description.Should().Be(original.IssueDetail.Description);
        deserialized.IssueDetail.Labels.Should().BeEquivalentTo(original.IssueDetail.Labels);

        // ParsedIssue
        deserialized.ParsedIssue.RequirementsSection.Should().Be(original.ParsedIssue.RequirementsSection);
        deserialized.ParsedIssue.AcceptanceCriteria.Should().BeEquivalentTo(original.ParsedIssue.AcceptanceCriteria);

        // IssueComments
        deserialized.IssueComments.Should().HaveCount(2);
        deserialized.IssueComments[0].Id.Should().Be("comment-1");
        deserialized.IssueComments[0].Body.Should().Be("First comment with context");
        deserialized.IssueComments[0].Author.Should().Be("reviewer-one");
        deserialized.IssueComments[0].CreatedAt.Should().Be(original.IssueComments[0].CreatedAt);
        deserialized.IssueComments[1].Id.Should().Be("comment-2");

        // LinkedPullRequest
        deserialized.LinkedPullRequest.Should().NotBeNull();
        deserialized.LinkedPullRequest!.Number.Should().Be(99);
        deserialized.LinkedPullRequest.BranchName.Should().Be("feature/widget-factory");
        deserialized.LinkedPullRequest.Url.Should().Be("https://github.com/org/repo/pull/99");
        deserialized.LinkedPullRequest.IsDraft.Should().BeTrue();
        deserialized.LinkedPullRequest.IsMergeable.Should().BeFalse();
        deserialized.LinkedPullRequest.ReviewComments.Should().HaveCount(1);
        deserialized.LinkedPullRequest.ReviewComments[0].Id.Should().Be("rc-1");
        deserialized.LinkedPullRequest.ReviewComments[0].Path.Should().Be("src/WidgetFactory.cs");

        // ProviderConfigs
        deserialized.ProviderConfigs.Should().HaveCount(2);
        var repoConfig = deserialized.ProviderConfigs[0];
        repoConfig.Id.Should().Be("pc-repo-1");
        repoConfig.Kind.Should().Be(ProviderKind.Repository);
        repoConfig.ProviderType.Should().Be("GitHub");
        repoConfig.DisplayName.Should().Be("Main Work Repo");
        repoConfig.Settings.Should().ContainKey("token");
        repoConfig.RepositoryRole.Should().Be(RepositoryRole.Work);
        repoConfig.RequiredLabels.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
        repoConfig.BlacklistedPaths.Should().BeEquivalentTo(new[] { "docs/", ".github/" });
        repoConfig.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
        repoConfig.Secrets.Should().ContainKey("NPM_TOKEN");
        repoConfig.SetupSteps.Should().HaveCount(1);
        repoConfig.SetupSteps![0].Name.Should().Be("Install deps");
        repoConfig.SetupSteps[0].Command.Should().Be("dotnet restore");
        repoConfig.SteeringContent.Should().Be("# Repo steering\nFollow TDD.");

        // PipelineConfiguration (spot-check key properties)
        deserialized.PipelineConfiguration.MaxRetries.Should().Be(3);
        deserialized.PipelineConfiguration.MaxAnalysisRetries.Should().Be(2);
        deserialized.PipelineConfiguration.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        deserialized.PipelineConfiguration.WorkspaceBaseDirectory.Should().Be("/workspaces/agent");
        deserialized.PipelineConfiguration.BaselineHealthCheckEnabled.Should().BeFalse();
        deserialized.PipelineConfiguration.BrainReadOnly.Should().BeTrue();
        deserialized.PipelineConfiguration.MaxDecompositionSubIssues.Should().Be(8);

        // QualityGateConfigs
        deserialized.QualityGateConfigs.Should().HaveCount(1);
        var qg = deserialized.QualityGateConfigs[0];
        qg.Id.Should().Be("qg-1");
        qg.DisplayName.Should().Be("Build & Test");
        qg.MatchLabels.Should().BeEquivalentTo(new[] { "dotnet" });
        qg.CompilationCommand.Should().Be("dotnet");
        qg.CompilationArguments.Should().BeEquivalentTo(new[] { "build", "--no-restore" });
        qg.TestCommand.Should().Be("dotnet");
        qg.TestArguments.Should().BeEquivalentTo(new[] { "test", "--no-build" });
        qg.CoverageThreshold.Should().Be(80.0);
        qg.Enabled.Should().BeTrue();
        qg.ExecutionOrder.Should().Be(1);
        qg.CoverageReportFormat.Should().Be("cobertura");
        qg.CoverageReportPaths.Should().BeEquivalentTo(new[] { "TestResults/**/coverage.cobertura.xml" });
        qg.TestQuarantine.Should().NotBeNull();
        qg.TestQuarantine!.Enabled.Should().BeTrue();
        qg.TestQuarantine.MaxQuarantinedFailuresPerRun.Should().Be(3);
        qg.TestQuarantine.QuarantinedTests.Should().HaveCount(1);
        qg.TestQuarantine.QuarantinedTests[0].TestName.Should().Be("FlakyIntegrationTest");
        qg.TestQuarantine.QuarantinedTests[0].ExpiresAt.Should().NotBeNull();
        qg.TestQuarantine.QuarantinedTests[0].AssociatedSourceFiles.Should().BeEquivalentTo(new[] { "src/Integration/HttpClient.cs" });

        // McpServers
        deserialized.McpServers.Should().HaveCount(1);
        deserialized.McpServers[0].Name.Should().Be("context7");
        deserialized.McpServers[0].Type.Should().Be("stdio");
        deserialized.McpServers[0].Command.Should().Be("npx");
        deserialized.McpServers[0].Args.Should().BeEquivalentTo(new[] { "-y", "@context7/mcp" });
        deserialized.McpServers[0].Env.Should().ContainKey("NODE_ENV");
        deserialized.McpServers[0].AutoApprove.Should().BeEquivalentTo(new[] { "resolve-library-id" });

        // ReviewerConfigs
        deserialized.ReviewerConfigs.Should().HaveCount(1);
        deserialized.ReviewerConfigs[0].Id.Should().Be("rev-cfg-1");
        deserialized.ReviewerConfigs[0].DisplayName.Should().Be("Security Review");
        deserialized.ReviewerConfigs[0].MatchLabels.Should().BeEquivalentTo(new[] { "security" });
        deserialized.ReviewerConfigs[0].Agents.Should().HaveCount(1);
        deserialized.ReviewerConfigs[0].Agents[0].Name.Should().Be("SecurityReviewer");
        deserialized.ReviewerConfigs[0].Agents[0].Prompt.Should().Be("Review for vulnerabilities");

        // LinkedIssueContexts
        deserialized.LinkedIssueContexts.Should().NotBeNull();
        deserialized.LinkedIssueContexts!.Should().HaveCount(1);
        deserialized.LinkedIssueContexts[0].Identifier.Should().Be("org/repo#40");
        deserialized.LinkedIssueContexts[0].Title.Should().Be("Parent feature issue");
        deserialized.LinkedIssueContexts[0].Description.Should().Be("Original feature request description");

        // ProjectContext (DecompositionProjectContext)
        deserialized.ProjectContext.Should().NotBeNull();
        deserialized.ProjectContext!.ProjectName.Should().Be("Widget Platform");
        deserialized.ProjectContext.Repositories.Should().HaveCount(1);
        var repoTarget = deserialized.ProjectContext.Repositories[0];
        repoTarget.TemplateName.Should().Be("backend-api");
        repoTarget.Description.Should().Be("Core API service");
        repoTarget.DecompositionEnabled.Should().BeTrue();
        repoTarget.Available.Should().BeTrue();
        repoTarget.Labels.Should().BeEquivalentTo(new[] { "dotnet", "api" });
        repoTarget.IssueProviderId.Should().Be("ip-backend");
        repoTarget.RepoProviderId.Should().Be("rp-backend");
        repoTarget.LocalPath.Should().Be("repos/backend-api");

        // ProjectSecrets
        deserialized.ProjectSecrets.Should().NotBeNull();
        deserialized.ProjectSecrets!.Should().HaveCount(2);
        deserialized.ProjectSecrets["SHARED_API_KEY"].Should().Be("proj-secret-value");
        deserialized.ProjectSecrets["DB_CONNECTION"].Should().Be("Server=localhost;Database=widgets");

        // TraceContext
        deserialized.TraceContext.Should().NotBeNull();
        deserialized.TraceContext!.Should().HaveCount(2);
        deserialized.TraceContext["traceparent"].Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        deserialized.TraceContext["tracestate"].Should().Be("congo=t61rcWkgMzE");
    }

    /// <summary>
    /// Round-trip test for ChatPromptMessage with ALL properties populated.
    /// Verifies that MCP server configs and the McpConfigPath survive serialization.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Fact]
    public void ChatPromptMessage_RoundTrip_AllPropertiesPopulated()
    {
        var original = new ChatPromptMessage
        {
            SessionId = "chat-session-abc-123",
            Prompt = "Refactor the authentication module to use JWT tokens",
            UseResume = true,
            McpServers = new List<McpServerConfig>
            {
                new()
                {
                    Name = "context7",
                    Type = "stdio",
                    Command = "npx",
                    Args = new[] { "-y", "@context7/mcp" },
                    Url = null,
                    Env = new Dictionary<string, string> { ["NODE_ENV"] = "production" },
                    Disabled = false,
                    AutoApprove = new[] { "resolve-library-id", "query-docs" }
                },
                new()
                {
                    Name = "github-mcp",
                    Type = "sse",
                    Command = null,
                    Args = [],
                    Url = "https://mcp.github.com/sse",
                    Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_test" },
                    Disabled = false,
                    AutoApprove = new[] { "search_code" }
                }
            },
            McpConfigPath = "/home/ubuntu/.claude.json"
        };

        var deserialized = RoundTrip(original);

        deserialized.SessionId.Should().Be("chat-session-abc-123");
        deserialized.Prompt.Should().Be("Refactor the authentication module to use JWT tokens");
        deserialized.UseResume.Should().BeTrue();
        deserialized.McpConfigPath.Should().Be("/home/ubuntu/.claude.json");

        // McpServers
        deserialized.McpServers.Should().HaveCount(2);

        var mcp1 = deserialized.McpServers[0];
        mcp1.Name.Should().Be("context7");
        mcp1.Type.Should().Be("stdio");
        mcp1.Command.Should().Be("npx");
        mcp1.Args.Should().BeEquivalentTo(new[] { "-y", "@context7/mcp" });
        mcp1.Url.Should().BeNull();
        mcp1.Env.Should().ContainKey("NODE_ENV");
        mcp1.Env!["NODE_ENV"].Should().Be("production");
        mcp1.Disabled.Should().BeFalse();
        mcp1.AutoApprove.Should().BeEquivalentTo(new[] { "resolve-library-id", "query-docs" });

        var mcp2 = deserialized.McpServers[1];
        mcp2.Name.Should().Be("github-mcp");
        mcp2.Type.Should().Be("sse");
        mcp2.Command.Should().BeNull();
        mcp2.Args.Should().BeEmpty();
        mcp2.Url.Should().Be("https://mcp.github.com/sse");
        mcp2.Env.Should().ContainKey("GITHUB_TOKEN");
        mcp2.Disabled.Should().BeFalse();
        mcp2.AutoApprove.Should().BeEquivalentTo(new[] { "search_code" });
    }

    /// <summary>
    /// Round-trip test for ConsolidationJobMessage with ALL properties populated.
    /// Verifies nested ProviderConfigs, PipelineConfiguration, and trace context survive.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Fact]
    public void ConsolidationJobMessage_RoundTrip_AllPropertiesPopulated()
    {
        var lastSuccess = new DateTime(2026, 6, 8, 3, 0, 0, DateTimeKind.Utc);
        var original = new ConsolidationJobMessage
        {
            JobId = "consolidation-job-xyz-789",
            Type = ConsolidationRunType.HarnessSuggestions,
            TemplateId = "template-dotnet-10",
            TemplateName = "dotnet-10-backend",
            ProviderConfigs = new List<ProviderConfig>
            {
                new()
                {
                    Id = "pc-brain-1",
                    Kind = ProviderKind.Repository,
                    ProviderType = "GitHub",
                    DisplayName = "Brain Repo",
                    Settings = new Dictionary<string, string>
                    {
                        ["token"] = "ghp_brain_token",
                        ["owner"] = "org",
                        ["repo"] = "brain-repo"
                    },
                    RepositoryRole = RepositoryRole.Brain,
                    RequiredLabels = new[] { "brain" },
                    BlacklistedPaths = new[] { "secrets/" },
                    BlacklistMode = BlacklistMode.WarnAndExclude,
                    Secrets = new Dictionary<string, string> { ["API_KEY"] = "secret-123" },
                    SetupSteps = new List<SetupStep>
                    {
                        new() { Name = "Restore", Command = "dotnet restore" }
                    },
                    SteeringContent = "# Brain steering content"
                },
                new()
                {
                    Id = "pc-agent-2",
                    Kind = ProviderKind.Agent,
                    ProviderType = "KiroCli",
                    DisplayName = "Consolidation Agent",
                    Settings = new Dictionary<string, string> { ["model"] = "claude-sonnet" }
                }
            },
            PipelineConfiguration = new PipelineConfiguration
            {
                MaxRetries = 2,
                MaxAnalysisRetries = 1,
                IssuePageSize = 25,
                AgentTimeout = TimeSpan.FromMinutes(30),
                WorkspaceBaseDirectory = "/workspaces/consolidation",
                AnalysisReviewEnabled = false,
                BaselineHealthCheckEnabled = true,
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                ExternalCiPollInterval = TimeSpan.FromSeconds(15),
                MaxRefactoringProposals = 3,
                MaxDecompositionSubIssues = 5,
                MaxConcurrentDecompositions = 2,
                DecompositionTimeout = TimeSpan.FromMinutes(15),
                MaxOpenIssuesForContext = 10,
                BrainReadOnly = false
            },
            LastSuccessfulRunUtc = lastSuccess,
            FeedbackDataJson = "[{\"Outcome\":\"Success\",\"Category\":\"missing context\"}]",
            WorkspacePath = "/tmp/consolidation/workspace-abc",
            TraceContext = new Dictionary<string, string>
            {
                ["traceparent"] = "00-abcdef1234567890abcdef1234567890-1234567890abcdef-01",
                ["tracestate"] = "vendor=test"
            }
        };

        var deserialized = RoundTrip(original);

        // Top-level scalar properties
        deserialized.JobId.Should().Be("consolidation-job-xyz-789");
        deserialized.Type.Should().Be(ConsolidationRunType.HarnessSuggestions);
        deserialized.TemplateId.Should().Be("template-dotnet-10");
        deserialized.TemplateName.Should().Be("dotnet-10-backend");
        deserialized.LastSuccessfulRunUtc.Should().Be(lastSuccess);
        deserialized.FeedbackDataJson.Should().Be("[{\"Outcome\":\"Success\",\"Category\":\"missing context\"}]");
        deserialized.WorkspacePath.Should().Be("/tmp/consolidation/workspace-abc");

        // ProviderConfigs
        deserialized.ProviderConfigs.Should().HaveCount(2);

        var brainConfig = deserialized.ProviderConfigs[0];
        brainConfig.Id.Should().Be("pc-brain-1");
        brainConfig.Kind.Should().Be(ProviderKind.Repository);
        brainConfig.ProviderType.Should().Be("GitHub");
        brainConfig.DisplayName.Should().Be("Brain Repo");
        brainConfig.Settings.Should().ContainKey("token");
        brainConfig.Settings["owner"].Should().Be("org");
        brainConfig.Settings["repo"].Should().Be("brain-repo");
        brainConfig.RepositoryRole.Should().Be(RepositoryRole.Brain);
        brainConfig.RequiredLabels.Should().BeEquivalentTo(new[] { "brain" });
        brainConfig.BlacklistedPaths.Should().BeEquivalentTo(new[] { "secrets/" });
        brainConfig.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
        brainConfig.Secrets.Should().ContainKey("API_KEY");
        brainConfig.SetupSteps.Should().HaveCount(1);
        brainConfig.SetupSteps![0].Name.Should().Be("Restore");
        brainConfig.SetupSteps[0].Command.Should().Be("dotnet restore");
        brainConfig.SteeringContent.Should().Be("# Brain steering content");

        var agentConfig = deserialized.ProviderConfigs[1];
        agentConfig.Id.Should().Be("pc-agent-2");
        agentConfig.Kind.Should().Be(ProviderKind.Agent);
        agentConfig.ProviderType.Should().Be("KiroCli");
        agentConfig.DisplayName.Should().Be("Consolidation Agent");
        agentConfig.Settings.Should().ContainKey("model");

        // PipelineConfiguration
        deserialized.PipelineConfiguration.MaxRetries.Should().Be(2);
        deserialized.PipelineConfiguration.MaxAnalysisRetries.Should().Be(1);
        deserialized.PipelineConfiguration.IssuePageSize.Should().Be(25);
        deserialized.PipelineConfiguration.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
        deserialized.PipelineConfiguration.WorkspaceBaseDirectory.Should().Be("/workspaces/consolidation");
        deserialized.PipelineConfiguration.AnalysisReviewEnabled.Should().BeFalse();
        deserialized.PipelineConfiguration.BaselineHealthCheckEnabled.Should().BeTrue();
        deserialized.PipelineConfiguration.ExternalCiTimeout.Should().Be(TimeSpan.FromMinutes(5));
        deserialized.PipelineConfiguration.ExternalCiPollInterval.Should().Be(TimeSpan.FromSeconds(15));
        deserialized.PipelineConfiguration.MaxRefactoringProposals.Should().Be(3);
        deserialized.PipelineConfiguration.MaxDecompositionSubIssues.Should().Be(5);
        deserialized.PipelineConfiguration.MaxConcurrentDecompositions.Should().Be(2);
        deserialized.PipelineConfiguration.DecompositionTimeout.Should().Be(TimeSpan.FromMinutes(15));
        deserialized.PipelineConfiguration.MaxOpenIssuesForContext.Should().Be(10);
        deserialized.PipelineConfiguration.BrainReadOnly.Should().BeFalse();

        // TraceContext
        deserialized.TraceContext.Should().NotBeNull();
        deserialized.TraceContext!.Should().HaveCount(2);
        deserialized.TraceContext["traceparent"].Should().Be("00-abcdef1234567890abcdef1234567890-1234567890abcdef-01");
        deserialized.TraceContext["tracestate"].Should().Be("vendor=test");
    }

    /// <summary>
    /// Verifies MessagePack serializes AnalysisGateResult as its underlying integer value,
    /// NOT as a string. This is the default behavior with ContractlessStandardResolverAllowPrivate.
    ///
    /// CONTRACT: MessagePack transmits enums as int ordinals (Ready=0, NotReady=1, WontDo=2).
    /// The [JsonStringEnumMemberName] attributes on AnalysisGateResult only affect System.Text.Json
    /// serialization (REST APIs, config files) — they have no effect on MessagePack (SignalR hub).
    ///
    /// Both orchestrator and agent use the same C# enum type, so int-based serialization is safe.
    /// If a non-.NET consumer ever reads these messages, it must map integers to enum names.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Fact]
    public void AnalysisGateResult_MessagePack_SerializesAsInteger()
    {
        // Serialize each enum value independently to inspect the raw bytes
        var readyBytes = MessagePackSerializer.Serialize(AnalysisGateResult.Ready, MsgPackOptions);
        var notReadyBytes = MessagePackSerializer.Serialize(AnalysisGateResult.NotReady, MsgPackOptions);
        var wontDoBytes = MessagePackSerializer.Serialize(AnalysisGateResult.WontDo, MsgPackOptions);

        // Deserialize as int to prove the wire format is integer, not string
        var readyAsInt = MessagePackSerializer.Deserialize<int>(readyBytes, MsgPackOptions);
        var notReadyAsInt = MessagePackSerializer.Deserialize<int>(notReadyBytes, MsgPackOptions);
        var wontDoAsInt = MessagePackSerializer.Deserialize<int>(wontDoBytes, MsgPackOptions);

        readyAsInt.Should().Be(0);
        notReadyAsInt.Should().Be(1);
        wontDoAsInt.Should().Be(2);

        // Confirm round-trip deserialization back to the enum type also works correctly
        var readyRoundTrip = MessagePackSerializer.Deserialize<AnalysisGateResult>(readyBytes, MsgPackOptions);
        var notReadyRoundTrip = MessagePackSerializer.Deserialize<AnalysisGateResult>(notReadyBytes, MsgPackOptions);
        var wontDoRoundTrip = MessagePackSerializer.Deserialize<AnalysisGateResult>(wontDoBytes, MsgPackOptions);

        readyRoundTrip.Should().Be(AnalysisGateResult.Ready);
        notReadyRoundTrip.Should().Be(AnalysisGateResult.NotReady);
        wontDoRoundTrip.Should().Be(AnalysisGateResult.WontDo);

        // Verify string deserialization is NOT possible (proving it's int on the wire).
        // Attempting to deserialize a MessagePack-encoded string "ready" as AnalysisGateResult would fail.
        var stringBytes = MessagePackSerializer.Serialize("ready", MsgPackOptions);
        var deserializeStringAsEnum = () => MessagePackSerializer.Deserialize<AnalysisGateResult>(stringBytes, MsgPackOptions);
        deserializeStringAsEnum.Should().Throw<MessagePackSerializationException>();
    }

    /// <summary>
    /// Verifies AnalysisGateResult round-trips correctly when nested inside a DTO (JobCompletionPayload),
    /// matching the real SignalR transmission path.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Fact]
    public void AnalysisGateResult_RoundTripsCorrectly_WhenNestedInDto()
    {
        // Test all three enum values in their natural container
        var baseTime = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var payloads = new[]
        {
            new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = baseTime, AnalysisRecommendation = AnalysisGateResult.Ready },
            new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = baseTime, AnalysisRecommendation = AnalysisGateResult.NotReady },
            new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = baseTime, AnalysisRecommendation = AnalysisGateResult.WontDo }
        };

        foreach (var original in payloads)
        {
            var deserialized = RoundTrip(original);
            deserialized.AnalysisRecommendation.Should().Be(original.AnalysisRecommendation);
        }
    }
}
