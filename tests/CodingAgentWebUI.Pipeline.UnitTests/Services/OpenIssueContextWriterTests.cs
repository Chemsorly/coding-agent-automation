using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="OpenIssueContextWriter"/>.
/// Tests file format, directory creation, cap enforcement, and graceful degradation.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 8.1, 8.2, 8.5, 8.7
/// </summary>
public class OpenIssueContextWriterTests : IDisposable
{
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;
    private readonly OpenIssueContextWriter _writer;

    public OpenIssueContextWriterTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"open-issue-writer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _writer = new OpenIssueContextWriter(_logger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_CreatesDirectory()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = Array.Empty<IssueSummary>(),
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.Exists(outputDir).Should().BeTrue();
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_WritesCorrectFileFormat()
    {
        var issueSummary = new IssueSummary
        {
            Identifier = "42",
            Title = "Add pagination",
            Labels = new[] { "agent:next", "enhancement" }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[] { issueSummary },
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Add pagination",
                Description = "Implement pagination for the /users endpoint.",
                Labels = new[] { "agent:next", "enhancement" }
            });

        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        count.Should().Be(1);

        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "42.md");
        File.Exists(filePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("---");
        content.Should().Contain("identifier: \"42\"");
        content.Should().Contain("title: \"Add pagination\"");
        content.Should().Contain("labels: [\"agent:next\", \"enhancement\"]");
        content.Should().Contain("Implement pagination for the /users endpoint.");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EnforcesMaxIssuesCap()
    {
        var issues = Enumerable.Range(1, 5).Select(i => new IssueSummary
        {
            Identifier = i.ToString(),
            Title = $"Issue {i}",
            Labels = Array.Empty<string>()
        }).ToList();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = issues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        foreach (var issue in issues)
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = issue.Identifier,
                    Title = issue.Title,
                    Description = $"Body of issue {issue.Identifier}",
                    Labels = Array.Empty<string>()
                });
        }

        // Cap at 3
        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 3, CancellationToken.None);

        count.Should().Be(3);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_IndividualFetchFailure_ContinuesWithOthers()
    {
        var issues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "2", Title = "Issue 2", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "3", Title = "Issue 3", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = issues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "1", Title = "Issue 1", Description = "Body 1", Labels = Array.Empty<string>() });
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));
        _issueOps.Setup(x => x.GetIssueAsync("3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "3", Title = "Issue 3", Description = "Body 3", Labels = Array.Empty<string>() });

        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        // Should write 2 out of 3 (issue 2 failed)
        count.Should().Be(2);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_ListingFails_ReturnsZero()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_NoOpenIssues_ReturnsZero()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = Array.Empty<IssueSummary>(),
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_MaxIssuesLessThanOne_ClampsToOne()
    {
        var issue = new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = Array.Empty<string>() };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[] { issue },
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "1", Title = "Issue 1", Description = "Body", Labels = Array.Empty<string>() });

        // Pass 0 — should clamp to 1
        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 0, CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public void FormatIssueMarkdown_ProducesCorrectYamlFrontMatter()
    {
        var detail = new IssueDetail
        {
            Identifier = "99",
            Title = "Test issue with \"quotes\"",
            Description = "The body content",
            Labels = new[] { "bug", "priority:high" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail);

        markdown.Should().Contain("---");
        markdown.Should().Contain("identifier: \"99\"");
        markdown.Should().Contain("title: \"Test issue with \\\"quotes\\\"\"");
        markdown.Should().Contain("labels: [\"bug\", \"priority:high\"]");
        markdown.Should().Contain("The body content");
    }

    [Fact]
    public void FormatIssueMarkdown_EmptyLabels_ProducesEmptyArray()
    {
        var detail = new IssueDetail
        {
            Identifier = "1",
            Title = "Simple",
            Description = "Body",
            Labels = Array.Empty<string>()
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail);

        markdown.Should().Contain("labels: []");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_Pagination_FetchesMultiplePages()
    {
        var page1Issues = Enumerable.Range(1, 30).Select(i => new IssueSummary
        {
            Identifier = i.ToString(),
            Title = $"Issue {i}",
            Labels = Array.Empty<string>()
        }).ToList();

        var page2Issues = Enumerable.Range(31, 5).Select(i => new IssueSummary
        {
            Identifier = i.ToString(),
            Title = $"Issue {i}",
            Labels = Array.Empty<string>()
        }).ToList();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = page1Issues,
                Page = 1,
                PageSize = 30,
                HasMore = true
            });

        _issueOps.Setup(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = page2Issues,
                Page = 2,
                PageSize = 30,
                HasMore = false
            });

        // Setup GetIssueAsync for all 35 issues
        for (var i = 1; i <= 35; i++)
        {
            var id = i.ToString();
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = id,
                    Title = $"Issue {id}",
                    Description = $"Body {id}",
                    Labels = Array.Empty<string>()
                });
        }

        var count = await _writer.WriteOpenIssueContextAsync(_issueOps.Object, _workspacePath, 50, CancellationToken.None);

        count.Should().Be(35);
        _issueOps.Verify(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Closed sibling issue tests (CTX-01)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_IncludesClosedSiblings()
    {
        // Setup open issues
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "10", Title = "Open Issue 10", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "11", Title = "Open Issue 11", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        // Setup closed issues
        var closedIssues = new[]
        {
            new IssueSummary { Identifier = "5", Title = "Closed Issue 5", Labels = new[] { "agent:done" } },
            new IssueSummary { Identifier = "6", Title = "Closed Issue 6", Labels = new[] { "agent:done" } }
        };

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        // Setup GetIssueAsync for all issues
        foreach (var issue in openIssues.Concat(closedIssues))
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = issue.Identifier,
                    Title = issue.Title,
                    Description = $"Body of {issue.Title}",
                    Labels = issue.Labels
                });
        }

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 50, includeClosedSiblings: true, CancellationToken.None);

        // Should write 4 total (2 open + 2 closed)
        count.Should().Be(4);

        // Verify closed issues have status: closed in front-matter
        var closedFile = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "5.md"));
        closedFile.Should().Contain("status: closed");

        // Verify open issues do NOT have status: closed
        var openFile = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "10.md"));
        openFile.Should().NotContain("status: closed");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_NonEpicRun_DoesNotFetchClosedIssues()
    {
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Open Issue 1", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1",
                Title = "Open Issue 1",
                Description = "Body",
                Labels = Array.Empty<string>()
            });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 50, includeClosedSiblings: false, CancellationToken.None);

        count.Should().Be(1);

        // ListClosedIssuesAsync should never be called for non-epic runs
        _issueOps.Verify(
            x => x.ListClosedIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_RespectsTotalBudget()
    {
        // Max issues = 8 → open budget = 6 (8 - 8/4), closed budget = 2 (8/4)
        var openIssues = Enumerable.Range(1, 10).Select(i => new IssueSummary
        {
            Identifier = $"open-{i}",
            Title = $"Open Issue {i}",
            Labels = Array.Empty<string>()
        }).ToList();

        var closedIssues = Enumerable.Range(1, 5).Select(i => new IssueSummary
        {
            Identifier = $"closed-{i}",
            Title = $"Closed Issue {i}",
            Labels = Array.Empty<string>()
        }).ToList();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        // Setup GetIssueAsync for expected issues
        foreach (var issue in openIssues.Take(6))
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = issue.Identifier,
                    Title = issue.Title,
                    Description = $"Body {issue.Identifier}",
                    Labels = Array.Empty<string>()
                });
        }

        foreach (var issue in closedIssues.Take(2))
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = issue.Identifier,
                    Title = issue.Title,
                    Description = $"Body {issue.Identifier}",
                    Labels = Array.Empty<string>()
                });
        }

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 8, includeClosedSiblings: true, CancellationToken.None);

        // Total should be 6 open + 2 closed = 8 (respects budget)
        count.Should().Be(8);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_ClosedListingFails_StillWritesOpenIssues()
    {
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Open Issue 1", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1",
                Title = "Open Issue 1",
                Description = "Body",
                Labels = Array.Empty<string>()
            });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 50, includeClosedSiblings: true, CancellationToken.None);

        // Should still write the open issue despite closed listing failure
        count.Should().Be(1);
    }

    [Fact]
    public void FormatIssueMarkdown_ClosedIssue_IncludesStatusField()
    {
        var detail = new IssueDetail
        {
            Identifier = "99",
            Title = "Completed task",
            Description = "This was done",
            Labels = new[] { "agent:done" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail, isClosed: true);

        markdown.Should().Contain("status: closed");
        markdown.Should().Contain("identifier: \"99\"");
        markdown.Should().Contain("title: \"Completed task\"");
    }

    [Fact]
    public void FormatIssueMarkdown_OpenIssue_DoesNotIncludeStatusField()
    {
        var detail = new IssueDetail
        {
            Identifier = "100",
            Title = "Open task",
            Description = "This is in progress",
            Labels = new[] { "agent:next" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail, isClosed: false);

        markdown.Should().NotContain("status:");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_DeduplicatesClosedFromOpen()
    {
        // Issue "1" appears in both open and closed — should only be written once (as open)
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = Array.Empty<string>() }
        };

        var closedIssues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "2", Title = "Closed Issue 2", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "1", Title = "Issue 1", Description = "Body 1", Labels = Array.Empty<string>() });
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "2", Title = "Closed Issue 2", Description = "Body 2", Labels = Array.Empty<string>() });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 50, includeClosedSiblings: true, CancellationToken.None);

        // Issue "1" from closed list is deduplicated since it's already in open
        // TODO: Assert that "1.md" does NOT contain "status: closed" to verify the open version
        // was kept rather than the closed version during deduplication.
        count.Should().Be(2); // 1 open + 1 closed (deduplicated "1" removed from closed)
    }

    // TODO: Add test for WriteOpenIssueContextStep.IsEpicScopedRun() routing logic to verify
    // correct boolean is derived from PipelineRunType (acceptance criteria: "Unit test: epic run
    // includes closed siblings, standalone run does not").

    // ──────────────────────────────────────────────────────────────────────────
    // Budget edge-case tests (maxIssues=1,2,3 with includeClosedSiblings=true)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesOne_AllocatesSlotToOpen()
    {
        // maxIssues=1 with includeClosedSiblings=true: open must get at least 1 slot
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "open-1", Title = "Open Issue 1", Labels = Array.Empty<string>() }
        };

        var closedIssues = new[]
        {
            new IssueSummary { Identifier = "closed-1", Title = "Closed Issue 1", Labels = new[] { "agent:done" } }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "open-1",
                Title = "Open Issue 1",
                Description = "Body of open issue",
                Labels = Array.Empty<string>()
            });

        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "closed-1",
                Title = "Closed Issue 1",
                Description = "Body of closed issue",
                Labels = new[] { "agent:done" }
            });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 1, includeClosedSiblings: true, CancellationToken.None);

        // Should write exactly 1 issue: the open one (closed budget = 0 when maxIssues = 1)
        count.Should().Be(1);

        var openFile = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-1.md");
        File.Exists(openFile).Should().BeTrue();

        var openContent = await File.ReadAllTextAsync(openFile);
        openContent.Should().NotContain("status: closed");

        // Closed issue should NOT be written
        var closedFile = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md");
        File.Exists(closedFile).Should().BeFalse();
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesTwo_BothOpenAndClosed()
    {
        // TODO: This test does not guard the regression — old formula also yields closedBudget=1, openBudget=1 for maxIssues=2. Consider a parameterized boundary that only the new formula satisfies.
        // maxIssues=2 with includeClosedSiblings=true: 1 open + 1 closed (regression test)
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "open-1", Title = "Open Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "open-2", Title = "Open Issue 2", Labels = Array.Empty<string>() }
        };

        var closedIssues = new[]
        {
            new IssueSummary { Identifier = "closed-1", Title = "Closed Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "closed-2", Title = "Closed Issue 2", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "open-1",
                Title = "Open Issue 1",
                Description = "Body open 1",
                Labels = Array.Empty<string>()
            });

        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "closed-1",
                Title = "Closed Issue 1",
                Description = "Body closed 1",
                Labels = Array.Empty<string>()
            });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 2, includeClosedSiblings: true, CancellationToken.None);

        // Should write 2 total: 1 open + 1 closed
        count.Should().Be(2);

        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-1.md")).Should().BeTrue();
        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md")).Should().BeTrue();

        var closedContent = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md"));
        closedContent.Should().Contain("status: closed");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesThree_OpenGetsPriority()
    {
        // TODO: This test does not guard the regression — old formula also yields closedBudget=1, openBudget=2 for maxIssues=3. Consider a parameterized boundary that only the new formula satisfies.
        // maxIssues=3 with includeClosedSiblings=true: 2 open + 1 closed (regression test)
        var openIssues = new[]
        {
            new IssueSummary { Identifier = "open-1", Title = "Open Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "open-2", Title = "Open Issue 2", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "open-3", Title = "Open Issue 3", Labels = Array.Empty<string>() }
        };

        var closedIssues = new[]
        {
            new IssueSummary { Identifier = "closed-1", Title = "Closed Issue 1", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "closed-2", Title = "Closed Issue 2", Labels = Array.Empty<string>() },
            new IssueSummary { Identifier = "closed-3", Title = "Closed Issue 3", Labels = Array.Empty<string>() }
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = openIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = closedIssues,
                Page = 1,
                PageSize = 30,
                HasMore = false
            });

        // Setup GetIssueAsync for the 2 open + 1 closed that should be fetched
        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "open-1",
                Title = "Open Issue 1",
                Description = "Body open 1",
                Labels = Array.Empty<string>()
            });

        _issueOps.Setup(x => x.GetIssueAsync("open-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "open-2",
                Title = "Open Issue 2",
                Description = "Body open 2",
                Labels = Array.Empty<string>()
            });

        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "closed-1",
                Title = "Closed Issue 1",
                Description = "Body closed 1",
                Labels = Array.Empty<string>()
            });

        var count = await _writer.WriteOpenIssueContextAsync(
            _issueOps.Object, _workspacePath, 3, includeClosedSiblings: true, CancellationToken.None);

        // Should write 3 total: 2 open + 1 closed
        count.Should().Be(3);

        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-1.md")).Should().BeTrue();
        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-2.md")).Should().BeTrue();
        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md")).Should().BeTrue();

        // open-3 should NOT be written (over budget)
        File.Exists(Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-3.md")).Should().BeFalse();

        var closedContent = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md"));
        closedContent.Should().Contain("status: closed");
    }
}
