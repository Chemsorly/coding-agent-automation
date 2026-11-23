using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for how text by authors outside the repository is marked on the routes other than the run's own issue
/// and its comments: open-issue files, linked issue files, the review subject, the triage report, the failure
/// feedback prompt, titles and image alt text. Verified text must come out unchanged on every route.
/// </summary>
public sealed class UnverifiedOtherSourcesTests : IDisposable
{
    private const string VerifiedBody = "Keep <!-- note --> \u200Bthis.";
    private const string VerifiedTitle = "Fix \u26A0\uFE0F";
    private const string OutsideBody = "Real text.<!-- hidden -->";
    private const string OutsideTitle = "Fix\u200B it";
    private const string CriteriaWithHiddenItem = "## Acceptance Criteria\n- [ ] Real\n<!--\n- [ ] Hidden\n-->";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"unverified-other-{Guid.NewGuid():N}");

    public UnverifiedOtherSourcesTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    // ── Open-issue files: decomposition ──────────────────────────────────────

    [Fact]
    public void FormatIssueMarkdown_VerifiedAuthor_IsUnchanged()
    {
        var result = OpenIssueContextWriter.FormatIssueMarkdown(Detail(VerifiedTitle, VerifiedBody, verified: true));

        result.ReplaceLineEndings("\n").Should().Be(
            "---\nidentifier: \"7\"\ntitle: \"Fix \u26A0\uFE0F\"\nlabels: []\n---\n\nKeep <!-- note --> \u200Bthis.");
    }

    [Fact]
    public void FormatIssueMarkdown_UnverifiedAuthor_IsUnderUnverifiedSection()
    {
        var result = OpenIssueContextWriter.FormatIssueMarkdown(Detail(OutsideTitle, OutsideBody, verified: false));

        AssertNoHiddenText(result);
        result.ReplaceLineEndings("\n").Should().Be(
            "---\nidentifier: \"7\"\ntitle: \"Fix it\"\nlabels: []\n---\n\n## Unverified input\n\n"
            + UnverifiedInputText.SectionRule + "\n\nReal text.");
    }

    // ── Open-issue files: triage ─────────────────────────────────────────────

    [Fact]
    public async Task FormatIssue_VerifiedAuthor_IsUnchanged()
    {
        var file = await WriteOpenIssueFileAsync(Summary(VerifiedTitle, VerifiedBody, verified: true));

        file.ReplaceLineEndings("\n").Should().EndWith("---\n\n# Fix \u26A0\uFE0F\n\nKeep <!-- note --> \u200Bthis.\n");
    }

    [Fact]
    public async Task FormatIssue_UnverifiedAuthor_IsUnderUnverifiedSection()
    {
        var file = await WriteOpenIssueFileAsync(Summary(OutsideTitle, OutsideBody, verified: false));

        AssertNoHiddenText(file);
        file.Should().Contain("# Fix it").And.Contain("## Unverified input").And.Contain("Real text.");
    }

    [Fact]
    public async Task FormatIssue_UnverifiedAuthor_TruncatesAfterNormalizing()
    {
        var body = "<!-- x -->" + new string('a', 2_500);

        var file = await WriteOpenIssueFileAsync(Summary(OutsideTitle, body, verified: false));

        file.ReplaceLineEndings("\n").Should().Contain(new string('a', 2_000) + "\n…");
        file.Should().NotContain(new string('a', 2_001));
        AssertNoHiddenText(file);
    }

    // ── Linked issue files ───────────────────────────────────────────────────

    [Fact]
    public async Task LinkedIssueFile_UnverifiedAuthor_IsUnderUnverifiedSection()
    {
        await RunReviewStepAsync(ReviewRun(Linked(OutsideTitle, OutsideBody, verified: false)));

        var file = await File.ReadAllTextAsync(LinkedIssueFilePath());

        AssertNoHiddenText(file);
        file.Should().Be("# Issue #42: Fix it\n\n## Unverified input\n\n" + UnverifiedInputText.SectionRule + "\n\nReal text.");
    }

    [Fact]
    public async Task LinkedIssueFile_VerifiedAuthor_IsUnchanged()
    {
        await RunReviewStepAsync(ReviewRun(Linked(OutsideTitle, OutsideBody, verified: true)));

        var file = await File.ReadAllTextAsync(LinkedIssueFilePath());

        file.Should().Be("# Issue #42: Fix\u200B it\n\nReal text.<!-- hidden -->");
    }

    [Fact]
    public async Task LinkedIssueFile_UnverifiedAuthor_TruncatesAfterNormalizing()
    {
        var body = "<!--" + new string('x', 40_000) + "-->Real text.";

        await RunReviewStepAsync(ReviewRun(Linked(OutsideTitle, body, verified: false)));

        var file = await File.ReadAllTextAsync(LinkedIssueFilePath());

        file.Should().Contain("Real text.").And.NotContain("[... truncated ...]");
        AssertNoHiddenText(file);
    }

    // ── Review subject ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewSubject_UnverifiedLinkedIssue_ParsesNormalizedAndKeepsRawDescription()
    {
        var context = await RunReviewStepAsync(ReviewRun(Linked("Fix", CriteriaWithHiddenItem, verified: false)));

        context.ParsedIssue!.AcceptanceCriteria.Should().Equal("Real");
        context.ParsedIssue.RequirementsSection.Should().NotContain("Hidden");
        context.Issue!.Description.Should().Be(CriteriaWithHiddenItem);
        context.Issue.IsAuthorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewSubject_VerifiedLinkedIssue_ParsesRaw()
    {
        var context = await RunReviewStepAsync(
            ReviewRun(Linked("Fix", "## Acceptance Criteria\n- [ ] Real <!-- hidden -->", verified: true)));

        context.ParsedIssue!.AcceptanceCriteria.Should().Equal("Real <!-- hidden -->");
        context.Issue!.IsAuthorVerified.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewSubject_UnverifiedPrDescription_ParsesNormalized()
    {
        var run = ReviewRun(prDescription: CriteriaWithHiddenItem, prAuthorVerified: false);

        var context = await RunReviewStepAsync(run);

        context.ParsedIssue!.AcceptanceCriteria.Should().Equal("Real");
        context.ParsedIssue.RequirementsSection.Should().NotContain("Hidden");
        context.Issue!.Description.Should().Be(CriteriaWithHiddenItem);
        context.Issue.IsAuthorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewSubject_VerifiedPrDescription_ParsesRaw()
    {
        var run = ReviewRun(prDescription: "## Acceptance Criteria\n- [ ] Real <!-- hidden -->", prAuthorVerified: true);

        var context = await RunReviewStepAsync(run);

        context.ParsedIssue!.AcceptanceCriteria.Should().Equal("Real <!-- hidden -->");
        context.Issue!.IsAuthorVerified.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewSubject_ContextFile_HasDescriptionNoticeAndNotTheInformationRule()
    {
        var context = await RunReviewStepAsync(ReviewRun(Linked(OutsideTitle, CriteriaWithHiddenItem, verified: false)));

        var file = PromptBuilder.BuildIssueContextFileContent(context.Issue!, context.ParsedIssue!);

        foreach (var line in UnverifiedInputText.DescriptionNotice)
            file.Should().Contain(line);
        file.Should().NotContain("not instructions");
        file.Should().Contain("## Description").And.Contain("# Issue: Fix it");
        AssertNoHiddenText(file);
        file.Should().NotContain("Hidden").And.NotContain("<!--");
    }

    [Fact]
    public void BuildReviewPrompt_UnverifiedSubject_NormalizesTitle()
    {
        var prompt = PromptBuilder.BuildReviewPrompt(
            "Review.", Detail(OutsideTitle, OutsideBody, verified: false, identifier: "1"), Parsed(), ".agent/findings.md");

        prompt.Should().Contain("# Issue #1: Fix it");
        prompt.Should().NotContain("\u200B");
    }

    // ── Triage report ────────────────────────────────────────────────────────

    [Fact]
    public void TriageReport_UnverifiedAuthor_IsUnverifiedInputAndNotATask()
    {
        var issue = Detail("Checkout fails", OutsideBody, verified: false);

        var report = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), null, null, descriptionIsReport: true);

        AssertNoHiddenText(report);
        report.Should().Contain(UnverifiedInputText.SectionHeading)
            .And.Contain(UnverifiedInputText.SectionRule)
            .And.Contain(UnverifiedInputText.ReportRequirementsHeading)
            .And.Contain(UnverifiedInputText.ReportCriteriaHeading)
            .And.Contain("Real text.");
        report.Should().NotContain("## Description")
            .And.NotContain("your task")
            .And.NotContain("for the code change only");
    }

    [Fact]
    public void TriageReport_VerifiedAuthor_IsUnchanged()
    {
        var issue = Detail(VerifiedTitle, VerifiedBody, verified: true);

        var asReport = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), null, null, descriptionIsReport: true);
        var asTask = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), null, null, descriptionIsReport: false);

        asReport.Should().Be(asTask);
        asReport.Should().Contain("## Description").And.Contain("## Requirements").And.Contain("## Acceptance Criteria");
    }

    [Fact]
    public async Task WriteTriageContext_UnverifiedReport_UsesReportWording()
    {
        var context = TriageContext();
        context.Issue = Detail(OutsideTitle, OutsideBody, verified: false);

        var result = await new WriteTriageContextStep(null).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var report = await File.ReadAllTextAsync(Path.Combine(_workspace, AgentWorkspacePaths.IssueContextFilePath));
        AssertNoHiddenText(report);
        report.Should().Contain("## Unverified input").And.Contain("Real text.").And.NotContain("your task");
    }

    // ── Failure feedback prompt ──────────────────────────────────────────────

    [Fact]
    public void FeedbackPrompt_VerifiedAuthor_IsUnchanged()
    {
        var prompt = FeedbackPrompt(Detail(VerifiedTitle, VerifiedBody, verified: true));

        prompt.ReplaceLineEndings("\n").Should().Contain(
            "**Title:** Fix \u26A0\uFE0F\n\n**Description:**\nKeep <!-- note --> \u200Bthis.\n");
    }

    [Fact]
    public void FeedbackPrompt_UnverifiedAuthor_HasNoticeAndNormalizedText()
    {
        var prompt = FeedbackPrompt(Detail(OutsideTitle, OutsideBody, verified: false));

        AssertNoHiddenText(prompt);
        prompt.Should().Contain("**Title:** Fix it")
            .And.Contain(UnverifiedInputText.DescriptionNotice[0])
            .And.Contain("Real text.");
    }

    // ── Titles ───────────────────────────────────────────────────────────────

    [Fact]
    public void Title_VerifiedAuthor_IsRaw()
    {
        UnverifiedInputText.Title(Detail(VerifiedTitle, "Body", verified: true)).Should().Be(VerifiedTitle);
        UnverifiedInputText.Title(Summary(VerifiedTitle, "Body", verified: true)).Should().Be(VerifiedTitle);
    }

    [Fact]
    public void Title_UnverifiedAuthor_IsNormalized()
    {
        UnverifiedInputText.Title(Detail(OutsideTitle, "Body", verified: false)).Should().Be("Fix it");
        UnverifiedInputText.Title(Summary(OutsideTitle, "Body", verified: false)).Should().Be("Fix it");
    }

    [Fact]
    public void GetPromptIssueTitle_VerifiedSubject_IsRaw()
    {
        var run = Run(OutsideTitle, subjectVerified: true);

        run.GetPromptIssueTitle().Should().Be(OutsideTitle);
        run.IssueTitle.Should().Be(OutsideTitle);
    }

    [Fact]
    public void GetPromptIssueTitle_UnverifiedSubject_IsNormalized()
    {
        var run = Run(OutsideTitle, subjectVerified: false);

        run.GetPromptIssueTitle().Should().Be("Fix it");
        run.IssueTitle.Should().Be(OutsideTitle, "the branch name and the PR title use the raw title");
    }

    [Fact]
    public void BuildPrDescriptionPrompt_UnverifiedSubject_NormalizesTitle()
    {
        var prompt = PromptBuilder.BuildPrDescriptionPrompt(Run(OutsideTitle, subjectVerified: false));

        prompt.Should().Contain("**Issue:** #42 — Fix it");
        prompt.Should().NotContain("\u200B");
    }

    [Fact]
    public void BuildPrDescriptionPrompt_VerifiedSubject_KeepsRawTitle()
    {
        var prompt = PromptBuilder.BuildPrDescriptionPrompt(Run(VerifiedTitle, subjectVerified: true));

        prompt.Should().Contain("**Issue:** #42 — Fix \u26A0\uFE0F");
    }

    // TODO [WARNING] This test passes run.GetPromptIssueTitle() in itself, so it only shows that BuildReflectionPrompt prints
    // its argument. The call sites PullRequestFinalizationService (reflection) and AgentPhaseExecutor.CodeReview
    // (BuildReviewSummaryPrompt) have no test: reverting either to run.IssueTitle would leave every test green.
    [Fact]
    public void BuildReflectionPrompt_UnverifiedSubject_NormalizesTitleThroughItsArgument()
    {
        var run = Run(OutsideTitle, subjectVerified: false);

        var prompt = PromptBuilder.BuildReflectionPrompt(
            run, "No failures.", [], DateOnly.Parse("2026-01-01"), run.GetPromptIssueTitle());

        prompt.Should().Contain("- **Issue:** #42 — Fix it");
        prompt.Should().NotContain("\u200B");
    }

    [Fact]
    public void ConsolidationPromptBuilder_UnverifiedTitles_AreNormalized()
    {
        var scan = Summary("Scan\u200B issue", null, verified: false, identifier: "1");
        var other = Summary("Other\u200B issue", null, verified: false, identifier: "2");
        var done = Summary("Done\u200B issue", null, verified: false, identifier: "3", labels: [AgentLabels.Done]);
        var rejected = Summary("Rejected\u200B issue", null, verified: false, identifier: "4", labels: [AgentLabels.WontDo]);
        var feedback = new Dictionary<string, string> { ["3"] = "The scope was unclear." };

        var open = ConsolidationPromptBuilder.BuildOpenIssueContext([scan], [other]);
        var outcomes = ConsolidationPromptBuilder.BuildProposalOutcomeContext([done, rejected], feedback);

        open.Should().Contain("- #1 \"Scan issue\"").And.Contain("- #2 \"Other issue\"");
        outcomes.Should().Contain("- #3 \"Done issue\"")
            .And.Contain("- #4 \"Rejected issue\"")
            .And.Contain("- #3 \"Done issue\" — The scope was unclear.");
        open.Should().NotContain("\u200B");
        outcomes.Should().NotContain("\u200B");
    }

    [Fact]
    public void ConsolidationPromptBuilder_VerifiedTitles_AreRaw()
    {
        var open = ConsolidationPromptBuilder.BuildOpenIssueContext(
            [Summary(VerifiedTitle, null, verified: true, identifier: "1")], []);

        open.Should().Contain("- #1 \"Fix \u26A0\uFE0F\"");
    }

    // ── Image alt text ───────────────────────────────────────────────────────

    [Fact]
    public void BuildIssueContextFileContent_VerifiedWithImage_AltTextIsRaw()
    {
        var issue = Detail("Fix", "Body", verified: true);

        var file = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), null, Images());

        file.Should().Contain("| a\u200Bb |");
    }

    [Fact]
    public void BuildIssueContextFileContent_UnverifiedAuthor_NormalizesAltText()
    {
        var issue = Detail("Fix", "Body", verified: false);

        var file = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), null, Images());

        file.Should().Contain("| ab |").And.NotContain("\u200B");
    }

    [Fact]
    public void BuildIssueContextFileContent_UnverifiedComment_NormalizesAltText()
    {
        var issue = Detail("Fix", "Body", verified: true);
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "outsider", Body = "Hello", CreatedAt = DateTime.UnixEpoch, IsVerified = false }
        };

        var file = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), comments, Images());

        file.Should().Contain("| ab |").And.NotContain("\u200B");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void BuildIssueContextFileContent_VerifiedOrUnknownComment_KeepsAltTextRaw(bool? commentVerified)
    {
        var issue = Detail("Fix", "Body", verified: true);
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Author = "maintainer", Body = "Hello", CreatedAt = DateTime.UnixEpoch, IsVerified = commentVerified }
        };

        var file = PromptBuilder.BuildIssueContextFileContent(issue, Parsed(), comments, Images());

        file.Should().Contain("| a\u200Bb |", "a null IsVerified comes from an older API and counts as verified");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertNoHiddenText(string result)
    {
        result.Should().NotContain("hidden");
        result.Should().NotContain("\u200B");
    }

    private static IssueDetail Detail(string title, string description, bool verified, string identifier = "7") => new()
    {
        Identifier = identifier,
        Title = title,
        Description = description,
        Labels = [],
        IsAuthorVerified = verified
    };

    private static IssueSummary Summary(
        string title, string? description, bool verified, string identifier = "7", IReadOnlyList<string>? labels = null) => new()
    {
        Identifier = identifier,
        Title = title,
        Description = description,
        Labels = labels ?? [],
        IsAuthorVerified = verified
    };

    private static LinkedIssueContext Linked(string title, string description, bool verified) => new()
    {
        Identifier = "42",
        Title = title,
        Description = description,
        IsAuthorVerified = verified
    };

    private static ParsedIssue Parsed() => new()
    {
        RequirementsSection = "Parse the file.",
        AcceptanceCriteria = ["Real"]
    };

    private static List<DownloadedImage> Images() =>
    [
        new()
        {
            LocalPath = ".agent/images/issue-42-image-001.png",
            LocalFilename = "issue-42-image-001.png",
            Reference = new ImageReference
            {
                Url = "https://github.com/user-attachments/assets/abc123.png",
                AltText = "a\u200Bb",
                SourceType = ImageSourceType.Body,
                SourceIndex = 0
            },
            FileSizeBytes = 1024,
            MimeType = "image/png"
        }
    ];

    private static PipelineRun Run(string title, bool subjectVerified) => new()
    {
        RunId = "run-1",
        IssueIdentifier = "42",
        IssueTitle = title,
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        IsSubjectAuthorVerified = subjectVerified
    };

    private static string FeedbackPrompt(IssueDetail issue)
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "error CS1002" },
            Tests = new GateResult { GateName = "Tests", Passed = false, Details = "3 tests failed" }
        };

        return FeedbackPromptBuilder.BuildFailureFeedbackPrompt(Run(issue.Title, issue.IsAuthorVerified), issue, report, [], []);
    }

    private string LinkedIssueFilePath() => Path.Combine(_workspace, ".agent", "linked-issue-42.md");

    private PipelineRun ReviewRun(LinkedIssueContext linked) => ReviewRun([linked], prDescription: "PR body", prAuthorVerified: false);

    private PipelineRun ReviewRun(string prDescription, bool prAuthorVerified) =>
        ReviewRun([], prDescription, prAuthorVerified);

    private PipelineRun ReviewRun(IReadOnlyList<LinkedIssueContext> linked, string prDescription, bool prAuthorVerified) => new()
    {
        RunId = "review-run",
        IssueIdentifier = "10",
        IssueTitle = "Test PR",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        WorkspacePath = _workspace,
        RunType = PipelineRunType.Review,
        ReviewPrDescription = prDescription,
        ReviewPrAuthorVerified = prAuthorVerified,
        LinkedIssueContexts = linked
    };

    private async Task<PipelineStepContext> RunReviewStepAsync(PipelineRun run)
    {
        var context = StepContext(run, Mock.Of<IAgentIssueOperations>());
        await new ExtractLinkedIssuesStep(new IssueDescriptionParser()).ExecuteAsync(context, CancellationToken.None);
        return context;
    }

    private PipelineStepContext TriageContext(
        Mock<IAgentIssueOperations>? issueOps = null, DecompositionProjectContext? project = null)
    {
        var run = new PipelineRun
        {
            RunId = "triage-run",
            IssueIdentifier = "431",
            IssueTitle = "Checkout returns 502",
            IssueProviderConfigId = "",
            RepoProviderConfigId = "rp-api",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Triage,
            ProjectId = "p1",
            ProjectName = "Shop",
            WorkspacePath = _workspace
        };
        var context = StepContext(run, (issueOps ?? new Mock<IAgentIssueOperations>()).Object, project);
        context.Issue = Detail("Checkout returns 502", "One in five checkouts fails.", verified: true, identifier: "431");
        return context;
    }

    private PipelineStepContext StepContext(
        PipelineRun run, IAgentIssueOperations issueOps, DecompositionProjectContext? project = null) => new()
    {
        Run = run,
        Config = new PipelineConfiguration { WorkspaceBaseDirectory = _workspace, MaxOpenIssuesForContext = 50 },
        RepoProvider = Mock.Of<IRepositoryProvider>(),
        AgentProvider = Mock.Of<IAgentProvider>(),
        BrainProvider = null,
        PipelineProvider = null,
        Cts = null,
        ConfigStore = Mock.Of<IConfigurationStore>(),
        Callbacks = Mock.Of<IPipelineCallbacks>(),
        IssueOps = issueOps,
        AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
        QualityGates = Mock.Of<IQualityGateExecutor>(),
        BrainSync = null,
        PrOrchestrator = new PullRequestOrchestrator(Serilog.Log.Logger),
        Logger = Serilog.Log.Logger,
        ProjectContext = project
    };

    /// <summary>Runs <see cref="WriteTriageContextStep"/> with one tracker that lists <paramref name="issue"/>, and returns its file.</summary>
    private async Task<string> WriteOpenIssueFileAsync(IssueSummary issue)
    {
        var issueOps = new Mock<IAgentIssueOperations>();
        issueOps.Setup(o => o.ListOpenIssuesForProviderAsync("t-api", 1, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [issue], Page = 1, PageSize = 50, HasMore = false });
        var context = TriageContext(issueOps, new DecompositionProjectContext
        {
            ProjectName = "Shop",
            Repositories =
            [
                new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "rp-api", IssueProviderId = "t-api" }
            ]
        });

        var result = await new WriteTriageContextStep(null).ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var path = Directory.GetFiles(Path.Combine(_workspace, AgentWorkspacePaths.OpenIssuesDirectory)).Should().ContainSingle().Subject;
        return await File.ReadAllTextAsync(path);
    }
}
