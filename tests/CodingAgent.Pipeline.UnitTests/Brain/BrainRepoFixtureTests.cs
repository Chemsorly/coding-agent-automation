namespace CodingAgent.Pipeline.UnitTests.Brain;

using AwesomeAssertions;
using CodingAgent.Web.TestUtilities;
using Xunit;

/// <summary>
/// Smoke test for <see cref="BrainRepoFixture"/>: create, clone, commit, push from a second clone,
/// and verify a push from the first clone is rejected with non-fast-forward.
/// No Category trait — runs in CI and counts for Sonar (T12).
/// </summary>
public sealed class BrainRepoFixtureTests
{
    [Fact]
    public void Create_SeedsRemote_FilesReachable()
    {
        using var fixture = BrainRepoFixture.Create(
            new Dictionary<string, string> { ["SKILL.md"] = "# skill\n- a lesson\n" });

        var files = fixture.RemoteFiles();
        files.Should().ContainKey("SKILL.md");
        files["SKILL.md"].Should().Contain("a lesson");
    }

    [Fact]
    public void Clone_CommitAndPush_RemoteFilesUpdated()
    {
        using var fixture = BrainRepoFixture.Create(
            new Dictionary<string, string> { ["SKILL.md"] = "- original\n" });

        var clone = fixture.Clone("clone1");
        fixture.CommitAndPush(clone, new Dictionary<string, string?> { ["SKILL.md"] = "- updated\n" }, "update skill");

        var files = fixture.RemoteFiles();
        files["SKILL.md"].Should().Be("- updated\n");

        var messages = fixture.RemoteCommitMessages();
        messages.Should().HaveCountGreaterThanOrEqualTo(2);
        messages[0].Should().Be("update skill");
    }

    [Fact]
    public void Push_FromStaleClone_ThrowsNonFastForward()
    {
        using var fixture = BrainRepoFixture.Create(
            new Dictionary<string, string> { ["SKILL.md"] = "- original\n" });

        // clone1 pushes first; clone2's push is then rejected.
        var clone1 = fixture.Clone("clone1");
        var clone2 = fixture.Clone("clone2");

        fixture.CommitAndPush(clone1, new Dictionary<string, string?> { ["extra.md"] = "- new\n" }, "add extra");

        var act = () => fixture.CommitAndPush(
            clone2,
            new Dictionary<string, string?> { ["SKILL.md"] = "- changed from clone2\n" },
            "clone2 change");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-fast-forward*");
    }
}
