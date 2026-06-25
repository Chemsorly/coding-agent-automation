using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the PipelineSummary component.
/// </summary>
public class PipelineSummaryComponentTests : BunitContext
{
    private static PipelineRun CreateRun(
        PipelineStep step = PipelineStep.Completed,
        string? failureReason = null,
        string? prUrl = null,
        string? prNumber = null,
        bool isDraft = false,
        string? branchName = "feature/test-branch",
        string? repoName = "owner/repo",
        QualityGateReport? qr = null,
        int retryCount = 0,
        int filesChanged = 5,
        int linesAdded = 100,
        int linesRemoved = 20,
        IReadOnlyList<string>? blacklistedFiles = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Add input validation",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow.AddMinutes(-12).AddSeconds(-34),
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-12).AddSeconds(-34),
        CompletedAt = DateTime.UtcNow,
        CompletedAtOffset = DateTimeOffset.UtcNow,
        CurrentStep = step,
        FailureReason = failureReason,
        PullRequestUrl = prUrl,
        PullRequestNumber = prNumber,
        IsDraftPr = isDraft,
        BranchName = branchName,
        RepositoryName = repoName,
        LatestQualityReport = qr,
        RetryCount = retryCount,
        FilesChangedCount = filesChanged,
        LinesAdded = linesAdded,
        LinesRemoved = linesRemoved,
        BlacklistedFilesDetected = blacklistedFiles ?? Array.Empty<string>()
    };

    private static QualityGateReport CreateQualityReport(
        bool compilationPassed = true,
        bool testsPassed = true,
        int? testPassedCount = 181,
        int? testFailedCount = 0,
        int? testSkippedCount = 0,
        bool? coveragePassed = true,
        double? coveragePercent = 82.3,
        bool? securityPassed = null) => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = compilationPassed },
        Tests = new GateResult
        {
            GateName = "Tests", Passed = testsPassed,
            TestsPassed = testPassedCount, TestsFailed = testFailedCount, TestsSkipped = testSkippedCount
        },
        Coverage = coveragePassed.HasValue
            ? new GateResult { GateName = "Coverage", Passed = coveragePassed.Value, CoveragePercent = coveragePercent }
            : null,
        SecurityScan = securityPassed.HasValue
            ? new GateResult { GateName = "Security", Passed = securityPassed.Value }
            : null
    };

    [Fact]
    public void CompletedPipeline_ShowsSuccessIconAndStatus()
    {
        var run = CreateRun(prUrl: "https://github.com/pr/47", prNumber: "47", qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.MaxRetries, 3)
            .Add(s => s.OnBackToIssues, () => { }));

        // TODO: Use scoped selector `.summary-icon [data-icon="check-circle"]` to avoid ambiguity with result-strip icons
        Assert.NotNull(cut.Find("[data-icon=\"check-circle\"]"));
        Assert.Contains("Pipeline Completed", cut.Markup);
    }

    [Fact]
    public void FailedPipeline_ShowsFailureIconAndCallout()
    {
        var run = CreateRun(step: PipelineStep.Failed, failureReason: "Quality gates failed after max retries",
            qr: CreateQualityReport(testsPassed: false, testFailedCount: 7, coveragePassed: false, coveragePercent: 47.2));
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        // TODO: Use scoped selector `.summary-icon [data-icon="x-circle"]` to avoid ambiguity with result-strip icons
        Assert.NotNull(cut.Find("[data-icon=\"x-circle\"]"));
        Assert.Contains("Pipeline Failed", cut.Markup);
        Assert.Contains("Quality gates failed after max retries", cut.Markup);
        Assert.Contains("summary-failure-callout", cut.Markup);
    }

    [Fact]
    public void CancelledPipeline_ShowsCancelledStepAndNoResultStrip()
    {
        var run = CreateRun(step: PipelineStep.Cancelled, branchName: "feature/test");
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.NotNull(cut.Find("[data-icon=\"ban\"]"));
        Assert.Contains("Pipeline Cancelled", cut.Markup);
        Assert.Contains("Cancelled during:", cut.Markup);
        Assert.Contains("Generating Code", cut.Markup);
        Assert.DoesNotContain("result-strip", cut.Markup);
    }

    [Fact]
    public void Header_ShowsIssueInfo_Duration_Repo_Branch()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Issue #42", cut.Markup);
        Assert.Contains("Add input validation", cut.Markup);
        Assert.Contains("12m 34s", cut.Markup);
        Assert.Contains("owner/repo", cut.Markup);
        Assert.Contains("feature/test-branch", cut.Markup);
    }

    [Fact]
    public void Header_HidesBranchWhenNull()
    {
        var run = CreateRun(branchName: null, qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.DoesNotContain("Branch:", cut.Markup);
    }

    [Fact]
    public void Header_HidesRepoWhenNull()
    {
        var run = CreateRun(repoName: null, qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.DoesNotContain("Repo:", cut.Markup);
    }

    [Fact]
    public void ResultStrip_ShowsQualityGateMetrics()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.MaxRetries, 3)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Compilation", cut.Markup);
        Assert.NotNull(cut.Find(".result-strip [data-icon=\"check-circle\"]"));
        Assert.Contains("181p/0f/0s", cut.Markup);
        Assert.Contains("82.3%", cut.Markup);
        Assert.Contains("Files: 5 (+100 -20)", cut.Markup);
        Assert.Contains("Retries: 0/3", cut.Markup);
    }

    [Fact]
    public void ResultStrip_ShowsDashForNullCoverageAndSecurity()
    {
        var qr = CreateQualityReport(coveragePassed: null, securityPassed: null);
        var run = CreateRun(qr: qr);
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        // Coverage and Security should show "—" when null
        var markup = cut.Markup;
        Assert.Contains("Coverage —", markup);
        Assert.Contains("Security —", markup);
    }

    [Fact]
    public void ResultStrip_ShowsDashForNullTestCounts()
    {
        var qr = CreateQualityReport(testPassedCount: null, testFailedCount: null, testSkippedCount: null);
        var run = CreateRun(qr: qr);
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("—p/—f/—s", cut.Markup);
    }

    [Fact]
    public void PrLink_ShowsNumberAndFinalIndicator()
    {
        var run = CreateRun(prUrl: "https://github.com/pr/47", prNumber: "47", qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("PR #47", cut.Markup);
        Assert.Contains("(final)", cut.Markup);
        Assert.Contains("View on GitHub", cut.Markup);
    }

    [Fact]
    public void PrLink_ShowsDraftIndicator()
    {
        var run = CreateRun(prUrl: "https://github.com/pr/48", prNumber: "48", isDraft: true, qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("(draft)", cut.Markup);
    }

    [Fact]
    public void PrLink_HiddenWhenNoPr()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.DoesNotContain("View on GitHub", cut.Markup);
    }

    [Fact]
    public void BlacklistWarning_ShownWhenFilesDetected()
    {
        var run = CreateRun(qr: CreateQualityReport(),
            blacklistedFiles: new[] { "secrets.json", ".env" });
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Blacklisted files excluded", cut.Markup);
        Assert.Contains("secrets.json", cut.Markup);
        Assert.Contains(".env", cut.Markup);
    }

    [Fact]
    public void BlacklistWarning_HiddenWhenEmpty()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.DoesNotContain("Blacklisted files excluded", cut.Markup);
    }

    [Fact]
    public void RetriesDisplay_ShowsCountWithMax()
    {
        var run = CreateRun(retryCount: 2, qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.MaxRetries, 3)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Retries: 2/3", cut.Markup);
    }

    [Fact]
    public void RetriesDisplay_ShowsCountWithoutMaxWhenNull()
    {
        var run = CreateRun(retryCount: 1, qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Retries: 1", cut.Markup);
        Assert.DoesNotContain("Retries: 1/", cut.Markup);
    }

    [Fact]
    public void ResultStrip_ShowsDashesWhenNoQualityReport()
    {
        var run = CreateRun(qr: null);
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Compilation —", cut.Markup);
        Assert.Contains("Tests —", cut.Markup);
        Assert.Contains("Coverage —", cut.Markup);
        Assert.Contains("Security —", cut.Markup);
    }

    [Fact]
    public void BackToIssues_ButtonPresent()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("Back to Issues", cut.Markup);
    }

    [Fact]
    public void BackToIssues_InvokesCallback()
    {
        var called = false;
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => called = true));

        cut.Find("button.btn-save").Click();
        Assert.True(called);
    }

    [Fact]
    public void FailureCallout_HiddenWhenNoFailureReason()
    {
        var run = CreateRun(qr: CreateQualityReport());
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.DoesNotContain("summary-failure-callout", cut.Markup);
    }

    [Fact]
    public void DurationFormat_HoursMinutesSeconds()
    {
        var longRun = new PipelineRun
        {
            RunId = "test",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow.AddHours(-1).AddMinutes(-2).AddSeconds(-15),
            StartedAtOffset = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-2).AddSeconds(-15),
            CompletedAt = DateTime.UtcNow,
            CompletedAtOffset = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.Completed,
            LatestQualityReport = CreateQualityReport()
        };
        var cut = Render<PipelineSummary>(p => p
            .Add(s => s.Run, longRun)
            .Add(s => s.OnBackToIssues, () => { }));

        Assert.Contains("1h 02m 15s", cut.Markup);
    }
}
