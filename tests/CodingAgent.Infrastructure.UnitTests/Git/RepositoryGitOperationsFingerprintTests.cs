using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Xunit;
using AwesomeAssertions;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests for <see cref="RepositoryGitOperations.GetWorkingTreeFingerprint"/>.
/// Uses temp git repositories on a local path. No Category trait — runs in CI's unit suite (T12).
/// </summary>
public class RepositoryGitOperationsFingerprintTests : IDisposable
{
    private readonly string _repoPath;

    public RepositoryGitOperationsFingerprintTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"fingerprint-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        Repository.Init(_repoPath);
        using var repo = new Repository(_repoPath);
        var sig = new Signature("test", "test@test.com", DateTimeOffset.UtcNow);
        // Initial commit
        var seedFile = Path.Combine(_repoPath, "seed.txt");
        File.WriteAllText(seedFile, "seed");
        Commands.Stage(repo, "seed.txt");
        repo.Commit("init", sig, sig);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_repoPath)) return;
        foreach (var f in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EditedTrackedFile_IsIncluded()
    {
        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "changed");

        var result = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        result.Should().ContainKey("seed.txt");
        result["seed.txt"].Should().NotBe("<deleted>");
    }

    [Fact]
    public void NewUntrackedFile_IsIncluded()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new.txt"), "new content");

        var result = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        result.Should().ContainKey("new.txt");
    }

    [Fact]
    public void DeletedTrackedFile_ReturnsDeletedValue()
    {
        File.Delete(Path.Combine(_repoPath, "seed.txt"));

        var result = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        result.Should().ContainKey("seed.txt");
        result["seed.txt"].Should().Be("<deleted>");
    }

    [Fact]
    public void UnchangedFile_IsNotIncluded()
    {
        var result = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        result.Should().NotContainKey("seed.txt");
    }

    [Fact]
    public void TwoEqualFingerprints_AreEqual()
    {
        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "changed");
        var fp1 = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        // Same change, same fingerprint
        var fp2 = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        fp1.Should().BeEquivalentTo(fp2);
    }

    [Fact]
    public void ExcludedPath_IsNotIncluded()
    {
        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "changed");
        var excluded = Path.Combine(_repoPath, ".agent", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(excluded)!);
        File.WriteAllText(excluded, "agent stuff");

        var result = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, [".agent"]);

        result.Should().ContainKey("seed.txt");
        result.Keys.Should().NotContain(k => k.StartsWith(".agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DifferentContent_HasDifferentHash()
    {
        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "version1");
        var fp1 = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        File.WriteAllText(Path.Combine(_repoPath, "seed.txt"), "version2");
        var fp2 = RepositoryGitOperations.GetWorkingTreeFingerprint(_repoPath, []);

        fp1["seed.txt"].Should().NotBe(fp2["seed.txt"]);
    }
}
