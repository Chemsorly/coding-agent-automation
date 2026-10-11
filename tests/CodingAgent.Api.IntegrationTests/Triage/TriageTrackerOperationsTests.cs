using AwesomeAssertions;
using CodingAgent.Api.Triage;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// <see cref="TriageTrackerOperations"/>: what a tracker triage's issue gets when people act in the app, with every
/// value masked with the project's, repositories' and MCP servers' secrets before it is sanitized.
/// </summary>
public sealed class TriageTrackerOperationsTests
{
    private const string ProjectSecret = "hunter2-project-secret";
    private const string RepoSecret = "ghp_repo-token-value";
    private const string McpToken = "glsa_mcp-bearer-value";
    private const string ProfileMcpPassword = "profile-mcp-password";

    private static readonly PipelineProject Project = new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Shop",
        Secrets = new Dictionary<string, string> { ["DB_PASSWORD"] = ProjectSecret },
        McpServers =
        [
            new McpServerConfig
            {
                Name = "grafana",
                Type = "http",
                Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {McpToken}" },
            },
        ],
    };

    private readonly Mock<IConfigurationStore> _config = new();
    private readonly Mock<IProviderFactory> _providers = new();
    private readonly Mock<IIssueProvider> _issues = new();
    private readonly Mock<ILabelService> _labels = new();
    private readonly List<string> _comments = [];

    public TriageTrackerOperationsTests()
    {
        _config.Setup(c => c.GetProjectByIdAsync(Project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Project);
        _config.Setup(c => c.LoadTemplatesForProjectAsync(Project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PipelineJobTemplate { Id = "t-api", Name = "api", RepoProviderId = "rp-api", IssueProviderId = "tracker-1" }]);
        _config.Setup(c => c.GetProviderConfigByIdAsync("rp-api", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig
            {
                Id = "rp-api", DisplayName = "api", ProviderType = "GitHub", Kind = ProviderKind.Repository,
                Secrets = new Dictionary<string, string> { ["TOKEN"] = RepoSecret },
            });
        _config.Setup(c => c.GetProviderConfigByIdAsync("tracker-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderConfig { Id = "tracker-1", DisplayName = "tracker", ProviderType = "GitHub", Kind = ProviderKind.Issue });
        _config.Setup(c => c.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new AgentProfile
            {
                Id = "p", DisplayName = "p", AgentProviderConfigId = "a", Enabled = true,
                McpServers = [new McpServerConfig { Name = "db", Env = new Dictionary<string, string> { ["DB_PASSWORD"] = ProfileMcpPassword } }],
            },
        ]);

        _providers.Setup(p => p.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(_issues.Object);
        _issues.Setup(i => i.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _issues.Setup(i => i.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((IssueIdentifier _, string body, CancellationToken _) => _comments.Add(body))
            .ReturnsAsync("c1");
    }

    private TriageTrackerOperations Operations() => new(_config.Object, _providers.Object, _labels.Object, Serilog.Log.Logger);

    // ── Re-run ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestRerun_PostsTheMaskedFeedback_ThenSetsAgentTriage()
    {
        var feedback = new TriageFeedback
        {
            Text = $"  Also check the pool; password is {ProjectSecret} ",
            Answers =
            [
                new TriageAnswer { Question = "Which token?", Answer = $"{RepoSecret} and {McpToken}" },
                new TriageAnswer { Question = "DB?", Answer = ProfileMcpPassword },
            ],
            Author = "anna@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await Operations().RequestRerunAsync(Tracker(), feedback, CancellationToken.None);

        var comment = _comments.Should().ContainSingle().Subject;
        comment.Should().StartWith("**Feedback via Coding Agent** for the next triage attempt:")
            .And.Contain("Also check the pool")
            .And.Contain("- **Q:** Which token?")
            .And.NotContain(ProjectSecret)
            .And.NotContain(RepoSecret)
            .And.NotContain(McpToken)
            .And.NotContain(ProfileMcpPassword)
            .And.NotContain("anna", "the comment never names the app user");
        _labels.Verify(l => l.SwapLabelAsync(
            It.Is<ProviderConfigId>(p => p == "tracker-1"), It.Is<IssueIdentifier>(i => i == "431"), AgentLabels.Triage,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestRerun_WithoutText_PostsOnlyTheAnswers()
    {
        var feedback = new TriageFeedback
        {
            Answers = [new TriageAnswer { Question = "Which region?", Answer = "eu-west" }],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await Operations().RequestRerunAsync(Tracker(), feedback, CancellationToken.None);

        _comments.Single().Should().Contain("**A:** eu-west");
    }

    [Fact]
    public async Task RequestRerun_CommentFails_Throws_AndSetsNoLabel()
    {
        _issues.Setup(i => i.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("tracker down"));

        var act = () => Operations().RequestRerunAsync(Tracker(), Feedback("f"), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _labels.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RequestRerun_LabelFails_Throws()
    {
        _labels.Setup(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var act = () => Operations().RequestRerunAsync(Tracker(), Feedback("f"), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RequestRerun_TrackerNotConfigured_Throws()
    {
        _config.Setup(c => c.GetProviderConfigByIdAsync("tracker-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => Operations().RequestRerunAsync(Tracker(), Feedback("f"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    // ── Created / dismissed ──────────────────────────────────────────────────

    [Fact]
    public async Task ReportCreated_ListsTheIssues_ThenSetsAgentDone()
    {
        TriageCreatedIssue[] created =
        [
            Created("api", "12", "https://example.invalid/api/issues/12"),
            Created("web", "7", null),
        ];

        await Operations().ReportCreatedAsync(Tracker(), created, CancellationToken.None);

        _comments.Single().Should().StartWith(TriageConstants.SummaryMarker)
            .And.Contain("- api: https://example.invalid/api/issues/12")
            .And.Contain("- web: #7");
        VerifySwapped(AgentLabels.Done);
    }

    [Fact]
    public async Task ReportCreated_CommentFails_StillSetsAgentDone()
    {
        _issues.Setup(i => i.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("tracker down"));

        await Operations().ReportCreatedAsync(Tracker(), [Created("api", "12", null)], CancellationToken.None);

        VerifySwapped(AgentLabels.Done);
    }

    [Fact]
    public async Task ReportCreated_Cancelled_IsNotSwallowed()
    {
        _issues.Setup(i => i.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Operations().ReportCreatedAsync(Tracker(), [Created("api", "12", null)], CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReportDismissed_WithAReason_PostsItMasked_ThenSetsWontDo()
    {
        var triage = Tracker() with { DismissReason = $" Duplicate; see {ProjectSecret} " };

        await Operations().ReportDismissedAsync(triage, CancellationToken.None);

        _comments.Single().Should().StartWith("The triage was dismissed in Coding Agent: Duplicate")
            .And.NotContain(ProjectSecret);
        VerifySwapped(AgentLabels.WontDo);
    }

    [Fact]
    public async Task ReportDismissed_WithoutAReason_PostsTheShortNote()
    {
        await Operations().ReportDismissedAsync(Tracker(), CancellationToken.None);

        _comments.Single().Should().Be("The triage was dismissed in Coding Agent");
        VerifySwapped(AgentLabels.WontDo);
    }

    [Fact]
    public async Task ReportDismissed_LabelFails_IsNonFatal()
    {
        _labels.Setup(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var act = () => Operations().ReportDismissedAsync(Tracker(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void VerifySwapped(string label) =>
        _labels.Verify(l => l.SwapLabelAsync(
            It.Is<ProviderConfigId>(p => p == "tracker-1"), It.Is<IssueIdentifier>(i => i == "431"), label,
            LabelTargetKind.Issue, It.IsAny<CancellationToken>()), Times.Once);

    private static TriageRecord Tracker() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Project.Id,
        Source = TriageSource.Issue,
        IssueProviderConfigId = "tracker-1",
        IssueIdentifier = "431",
        Title = "Order confirmation spins forever",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static TriageFeedback Feedback(string text) => new() { Text = text, CreatedAt = DateTimeOffset.UtcNow };

    private static TriageCreatedIssue Created(string repository, string identifier, string? url) => new()
    {
        DraftId = "a1-d1",
        Repository = repository,
        IssueProviderConfigId = "tracker-1",
        Identifier = identifier,
        Url = url,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
