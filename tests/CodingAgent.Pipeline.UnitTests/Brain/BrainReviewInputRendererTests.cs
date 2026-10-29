namespace CodingAgent.Pipeline.UnitTests.Brain;

using AwesomeAssertions;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using Xunit;
using static CodingAgent.Pipeline.Brain.BrainReviewInputRenderer;

/// <summary>
/// Tests for <see cref="BrainReviewInputRenderer.Render"/> (spec 054, Task 7A.3).
/// Pure function — no I/O, no mocks.
/// </summary>
public sealed class BrainReviewInputRendererTests
{
    private static BrainInboxHeader MakeHeader(string runId = "run-1") =>
        new BrainInboxHeader(
            RunId: runId,
            RunType: "implementation",
            Outcome: "succeeded",
            RepositoryFullName: "org/repo",
            ProjectName: "My Project",
            StackLabels: ["dotnet", "blazor"],
            Issue: "#42",
            PullRequestUrl: "https://github.com/org/repo/pull/5",
            SkillsCommit: null,
            SkillsFiles: []);

    [Fact]
    public void Render_EmptyModel_ContainsSectionHeaders()
    {
        var model = new BrainReviewInputModel(
            Group: null,
            InboxFiles: [],
            MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [],
            HumanCommits: [],
            InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("# Brain review input");
        result.Should().Contain("## Brain group");
        result.Should().Contain("No group information available.");
        result.Should().Contain("## Inbox files");
        result.Should().Contain("## Skills at injected commits");
        result.Should().Contain("## Current skill files");
        result.Should().Contain("## Human commits since the last review");
        result.Should().Contain("No human commits.");
        // Malformed section is only rendered when there are malformed files
        result.Should().NotContain("## Malformed inbox files");
    }

    [Fact]
    public void Render_WithGroup_RendersGroupTable()
    {
        var group = new List<BrainGroupMember>
        {
            new BrainGroupMember
            {
                RepositoryFullName = "org/repo",
                RepositoryShortName = "repo",
                ProjectName = "My Project",
                StackLabels = ["dotnet"]
            }
        };
        var model = new BrainReviewInputModel(
            Group: group, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("| Repository | Short name | Project | Stack labels |");
        result.Should().Contain("org/repo");
        result.Should().Contain("dotnet");
        result.Should().NotContain("No group information available.");
    }

    [Fact]
    public void Render_WithInboxFiles_RendersInboxSection()
    {
        var inbox = new List<InboxFileEntry>
        {
            new InboxFileEntry("inbox/2026-10-29_run-1.md", MakeHeader()),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### inbox/2026-10-29_run-1.md");
        result.Should().Contain("run: run-1");
        result.Should().Contain("type: implementation");
        result.Should().Contain("outcome: succeeded");
        result.Should().Contain("repository: org/repo");
        result.Should().Contain("project: My Project");
        result.Should().Contain("issue: #42");
        result.Should().Contain("stack labels: dotnet, blazor");
    }

    [Fact]
    public void Render_InboxFileNoHeader_RendersPathOnly()
    {
        var inbox = new List<InboxFileEntry>
        {
            new InboxFileEntry("inbox/no-header.md", Header: null),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### inbox/no-header.md");
        result.Should().NotContain("run: ");
    }

    [Fact]
    public void Render_InboxFilesLeft_ShowsCount()
    {
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 42);

        var result = Render(model);

        result.Should().Contain("Inbox files left for the next review: 42");
    }

    [Fact]
    public void Render_WithMalformedInbox_RendersMalformedSection()
    {
        var malformed = new List<MalformedInboxEntry>
        {
            new MalformedInboxEntry("inbox/bad.md", "not UTF-8"),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: malformed,
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("## Malformed inbox files (excluded)");
        result.Should().Contain("inbox/bad.md: not UTF-8");
    }

    [Fact]
    public void Render_SkillsAtCommits_RendersCommitSection()
    {
        var skills = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>
        {
            ["abc1234567890"] = [new SkillFileAtCommit("general/SKILL.md", ["- use parameterized queries"])]
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: skills,
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### Commit abc123456789"); // 12-char truncation
        result.Should().Contain("#### general/SKILL.md");
        result.Should().Contain("- use parameterized queries");
    }

    [Fact]
    public void Render_UnknownCommit_ShowsUnknownPlaceholder()
    {
        var skills = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>
        {
            ["abc123"] = [] // no skill files = unknown commit
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: skills,
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("unknown commit");
    }

    [Fact]
    public void Render_CurrentFiles_RendersFileList()
    {
        var files = new List<CurrentSkillFile>
        {
            new CurrentSkillFile("general/SKILL.md", 15),
            new CurrentSkillFile("projects/org-repo/SKILL.md", 40),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: files, HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("- general/SKILL.md (15 lines)");
        result.Should().Contain("- projects/org-repo/SKILL.md (40 lines)");
    }

    [Fact]
    public void Render_HumanCommits_RendersCommitList()
    {
        var commits = new List<BrainCommitInfo>
        {
            new BrainCommitInfo("deadbeef12345", "user: fix typo"),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: commits, InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("deadbeef"); // 8-char truncation
        result.Should().Contain("user: fix typo");
        result.Should().NotContain("No human commits.");
    }

    [Fact]
    public void Render_InboxHeaderWithoutProject_OmitsProjectLine()
    {
        var header = new BrainInboxHeader(
            RunId: "r1", RunType: "implementation", Outcome: "succeeded",
            RepositoryFullName: "org/repo", ProjectName: null,
            StackLabels: [], Issue: "#1",
            PullRequestUrl: null, SkillsCommit: null, SkillsFiles: []);
        var inbox = new List<InboxFileEntry> { new InboxFileEntry("inbox/r1.md", header) };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().NotContain("project:");
        result.Should().NotContain("stack labels:");
    }
}
