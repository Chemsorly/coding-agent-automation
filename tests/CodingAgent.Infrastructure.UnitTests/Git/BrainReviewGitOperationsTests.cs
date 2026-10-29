using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Xunit;
using AwesomeAssertions;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for the review git operations added in spec 054 Task 7A.1.
/// Uses temp git repositories — no Category trait, runs in CI's unit suite (T12).
/// </summary>
public class BrainReviewGitOperationsTests : IDisposable
{
    private readonly string _repoPath;
    private readonly LibGit2SharpGitOperations _git = new();

    public BrainReviewGitOperationsTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"review-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        Repository.Init(_repoPath);
        using var repo = new Repository(_repoPath);
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "seed");
        Commands.Stage(repo, "seed.txt");
        repo.Commit("init", sig, sig);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_repoPath)) return;
        foreach (var f in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
    }

    [Fact]
    public void ListTrackedFiles_ReturnsTrackedPaths()
    {
        var files = _git.ListTrackedFiles(_repoPath);
        files.Should().Contain("seed.txt");
        files.Should().NotContain(p => p.Contains(".git"));
    }

    [Fact]
    public void ReadFileAtCommit_ExistingPath_ReturnsContent()
    {
        using var repo = new Repository(_repoPath);
        var sha = repo.Head.Tip.Sha;
        var content = _git.ReadFileAtCommit(_repoPath, sha, "seed.txt");
        content.Should().Be("seed");
    }

    [Fact]
    public void ReadFileAtCommit_UnknownSha_ReturnsNull()
    {
        var result = _git.ReadFileAtCommit(_repoPath, "0000000000000000000000000000000000000000", "seed.txt");
        result.Should().BeNull();
    }

    [Fact]
    public void ReadFileAtHead_ExistingFile_ReturnsContent()
    {
        var content = _git.ReadFileAtHead(_repoPath, "seed.txt");
        content.Should().Be("seed");
    }

    [Fact]
    public void ReadFileAtHead_MissingFile_ReturnsNull()
    {
        var result = _git.ReadFileAtHead(_repoPath, "no-such-file.md");
        result.Should().BeNull();
    }

    [Fact]
    public void GetWorkingTreeChanges_UntrackedFile_IncludedAsAdded()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new.md"), "new");
        var changes = _git.GetWorkingTreeChanges(_repoPath);
        changes.Should().Contain(c => c.Path == "new.md" && c.Kind == BrainPathChangeKind.Added);
    }

    [Fact]
    public void GetWorkingTreeChanges_UnchangedFile_NotIncluded()
    {
        var changes = _git.GetWorkingTreeChanges(_repoPath);
        changes.Should().NotContain(c => c.Path == "seed.txt");
    }

    [Fact]
    public void GetSkillCommitsSinceLastReview_NoReviewCommit_ReturnsEmpty()
    {
        // No commits touch skill dirs
        var result = _git.GetSkillCommitsSinceLastReview(_repoPath);
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetSkillCommitsSinceLastReview_WithSkillCommit_ReturnsThatCommit()
    {
        using var repo = new Repository(_repoPath);
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);

        // Add a skill file commit
        Directory.CreateDirectory(Path.Combine(_repoPath, "projects"));
        File.WriteAllText(Path.Combine(_repoPath, "projects", "SKILL.md"), "<!-- brain:v2 skill -->\n# Title");
        Commands.Stage(repo, "projects/SKILL.md");
        repo.Commit("feat: add project skill", sig, sig);

        var result = _git.GetSkillCommitsSinceLastReview(_repoPath);
        result.Should().HaveCount(1);
        result[0].Message.Should().Contain("feat: add project skill");
    }

    [Fact]
    public void GetSkillCommitsSinceLastReview_AfterReviewCommit_ReturnsEmpty()
    {
        using var repo = new Repository(_repoPath);
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);

        // Add a skill file THEN a review commit
        Directory.CreateDirectory(Path.Combine(_repoPath, "projects"));
        File.WriteAllText(Path.Combine(_repoPath, "projects", "SKILL.md"), "<!-- brain:v2 skill -->\n# Title");
        Commands.Stage(repo, "projects/SKILL.md");
        repo.Commit("feat: add skill", sig, sig);

        File.WriteAllText(Path.Combine(_repoPath, "projects", "SKILL.md"), "<!-- brain:v2 skill -->\n# Title v2");
        Commands.Stage(repo, "projects/SKILL.md");
        repo.Commit("brain: review run r1", sig, sig);

        var result = _git.GetSkillCommitsSinceLastReview(_repoPath);
        result.Should().BeEmpty("all skill commits are before the review commit");
    }
}
