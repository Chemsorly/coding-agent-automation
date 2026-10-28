namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

public class BrainLayoutTests
{
    // ── InboxFileName ─────────────────────────────────────────────────

    [Fact]
    public void InboxFileName_ReturnsCorrectPath()
    {
        var date = new DateOnly(2026, 10, 26);
        BrainLayout.InboxFileName(date, "run-1").Should().Be("inbox/2026-10-26_run-1.md");
    }

    // ── RepositorySkillPath ───────────────────────────────────────────

    [Fact]
    public void RepositorySkillPath_ReturnsProjectsPath()
    {
        BrainLayout.RepositorySkillPath("coding-agent-automation")
            .Should().Be("projects/coding-agent-automation/SKILL.md");
    }

    // ── TechnologySkillPath ───────────────────────────────────────────

    [Fact]
    public void TechnologySkillPath_LowercasesLabel()
    {
        BrainLayout.TechnologySkillPath("DotNet").Should().Be("technology/dotnet/SKILL.md");
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("")]
    public void TechnologySkillPath_InvalidLabel_ReturnsNull(string label)
    {
        BrainLayout.TechnologySkillPath(label).Should().BeNull();
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet10")]
    [InlineData("my-label")]
    [InlineData("my.label")]
    public void TechnologySkillPath_ValidLabel_ReturnsPath(string label)
    {
        BrainLayout.TechnologySkillPath(label).Should().NotBeNull();
    }

    // ── IsSkillPath ───────────────────────────────────────────────────

    [Theory]
    [InlineData("general/SKILL.md", true)]
    [InlineData("projects/x/SKILL.md", true)]
    [InlineData("technology/dotnet/SKILL.md", true)]
    [InlineData("projects/x/lessons.md", false)]
    [InlineData("general/lessons-learned.md", false)]
    [InlineData("inbox/sub/x.md", false)]
    [InlineData("AGENTS.md", false)]
    public void IsSkillPath_ReturnsExpected(string path, bool expected)
    {
        BrainLayout.IsSkillPath(path).Should().Be(expected);
    }

    // ── IsInboxPath ───────────────────────────────────────────────────

    [Theory]
    [InlineData("inbox/2026-01-01_abc.md", true)]
    [InlineData("inbox/sub/x.md", false)]
    [InlineData("general/SKILL.md", false)]
    public void IsInboxPath_ReturnsExpected(string path, bool expected)
    {
        BrainLayout.IsInboxPath(path).Should().Be(expected);
    }

    // ── IsV2Path ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("review/pending.md", true)]
    [InlineData("general/SKILL.md", true)]
    [InlineData("projects/x/SKILL.md", true)]
    [InlineData("technology/dotnet/SKILL.md", true)]
    [InlineData("inbox/2026-10-26_abc.md", true)]
    [InlineData("archive/v1/x.md", true)]
    [InlineData(".gitignore", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("sessions/x.md", false)]
    [InlineData("AGENTS.md", false)]
    [InlineData("log.md", false)]
    [InlineData("projects/x/lessons.md", false)]
    public void IsV2Path_ReturnsExpected(string path, bool expected)
    {
        BrainLayout.IsV2Path(path).Should().Be(expected);
    }

    // ── Commit messages ───────────────────────────────────────────────

    [Fact]
    public void InboxCommitMessage_Format()
    {
        BrainLayout.InboxCommitMessage("run-1", "#42")
            .Should().Be("brain: inbox from run run-1 (#42)");
    }

    [Fact]
    public void ReviewCommitMessage_Format()
    {
        BrainLayout.ReviewCommitMessage("rev-1").Should().Be("brain: review run rev-1");
    }
}
