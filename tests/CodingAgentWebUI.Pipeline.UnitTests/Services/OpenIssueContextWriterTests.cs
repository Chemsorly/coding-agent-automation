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
}
