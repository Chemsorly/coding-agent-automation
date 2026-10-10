using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using LibGit2Sharp;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Integration tests verifying that <see cref="RepositoryGitOperations.CommitAll"/>
/// always unstages .agent/ files regardless of the configured blacklist.
/// </summary>
[Trait("Category", "Integration")]
public class RepositoryGitOperationsCommitBlacklistTests : IDisposable
{
    private readonly string _workspacePath;

    public RepositoryGitOperationsCommitBlacklistTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"commit-blacklist-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        InitGitRepo();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    [Fact]
    public void CommitAll_NullBlacklist_StillUnstagesAgentDirectory()
    {
        // Arrange: create a file inside .agent/ and a normal file
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent", "settings"));
        File.WriteAllText(Path.Combine(_workspacePath, ".agent", "settings", "mcp.json"), "{\"mcpServers\":{}}");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "app.cs"), "// code");

        // Act
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit", blacklistedPaths: null, allowEmpty: false);

        // Assert: .agent file was unstaged
        unstaged.Should().Contain(f => f.StartsWith(".agent/"));

        // Verify the committed tree does NOT contain .agent files
        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".agent"].Should().BeNull();
        tree["src/app.cs"].Should().NotBeNull();
    }

    [Fact]
    public void CommitAll_EmptyBlacklist_StillUnstagesAgentDirectory()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));
        File.WriteAllText(Path.Combine(_workspacePath, ".agent", "prompt-input.md"), "prompt content");
        File.WriteAllText(Path.Combine(_workspacePath, "newfile.txt"), "updated content");

        // Act
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit", blacklistedPaths: Array.Empty<string>(), allowEmpty: false);

        // Assert
        unstaged.Should().Contain(f => f.StartsWith(".agent/"));

        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".agent"].Should().BeNull();
        tree["newfile.txt"].Should().NotBeNull();
    }

    [Fact]
    public void CommitAll_BlacklistWithoutAgent_StillUnstagesAgentDirectory()
    {
        // Arrange: blacklist only has .github, NOT .agent
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));
        File.WriteAllText(Path.Combine(_workspacePath, ".agent", "analysis.md"), "analysis content");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "main.cs"), "// main");

        // Act
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: new[] { ".github" }, allowEmpty: false);

        // Assert
        unstaged.Should().Contain(f => f.StartsWith(".agent/"));

        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".agent"].Should().BeNull();
    }

    [Fact]
    public void CommitAll_AgentInBlacklist_DoesNotDoubleReport()
    {
        // Arrange: .agent is in both the hardcoded list AND the configured blacklist
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));
        File.WriteAllText(Path.Combine(_workspacePath, ".agent", "data.json"), "{}");
        File.WriteAllText(Path.Combine(_workspacePath, "lib.cs"), "// lib");

        // Act
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: new[] { ".agent", ".github" }, allowEmpty: false);

        // Assert: .agent file appears exactly once in the unstaged list
        var agentEntries = unstaged.Where(f => f.StartsWith(".agent/")).ToList();
        agentEntries.Should().HaveCount(1);
        agentEntries[0].Should().Be(".agent/data.json");
    }

    // TODO: Add a test for deduplication in UnstageBlacklistedPaths when a path uses backslash ('\\')
    // separators and the file matches both the hardcoded and configurable blacklists. The guard
    // `unstaged.Contains(normalized)` uses the normalized form (forward slashes), but if path separator
    // handling were inconsistent between the two passes, a Windows-style path could evade deduplication
    // and Commands.Unstage would be called twice. No test currently covers this scenario.
    // Example: a file at ".agent\\data.json" matching both passes should appear exactly once in the
    // returned unstaged set and have Commands.Unstage called exactly once.

    [Fact]
    public void CommitAll_EditToAFileTheRepositoryTracksUnderAnInjectedPath_IsCommitted()
    {
        // Arrange: the repository tracks its own Kiro steering; the run edits it and the pipeline
        // adds its own steering file next to it.
        var steeringDir = Path.Combine(_workspacePath, ".kiro", "steering");
        Directory.CreateDirectory(steeringDir);
        File.WriteAllText(Path.Combine(steeringDir, "team.md"), "old rules");
        using (var repo = new Repository(_workspacePath))
        {
            Commands.Stage(repo, ".kiro/steering/team.md");
            var sig = new Signature("test", "test@test.com", DateTimeOffset.Now);
            repo.Commit("track team steering", sig, sig);
        }
        File.WriteAllText(Path.Combine(steeringDir, "team.md"), "new rules");
        File.WriteAllText(Path.Combine(steeringDir, "pipeline-repo.md"), "pipeline steering");

        // Act
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: null, allowEmpty: false,
            pipelineInjectedPaths: new[] { ".kiro" });

        // Assert: the repository's own file is committed, the pipeline's new file is not
        unstaged.Should().Equal(".kiro/steering/pipeline-repo.md");
        using var committed = new Repository(_workspacePath);
        var tree = committed.Head.Tip.Tree;
        ((Blob)tree[".kiro/steering/team.md"].Target).GetContentText().Should().Be("new rules");
        tree[".kiro/steering/pipeline-repo.md"].Should().BeNull();
    }

    [Fact]
    public void CommitAll_NullBlacklist_StillUnstagesKiroDirectory()
    {
        // Arrange: .kiro/steering/ contains pipeline-injected steering files
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".kiro", "steering"));
        File.WriteAllText(Path.Combine(_workspacePath, ".kiro", "steering", "pipeline-repo.md"), "steering content");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "app.cs"), "// code");

        // Act: pass .kiro as pipeline-injected path (from KiroCliAgentProvider.PipelineInjectedPaths)
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: null, allowEmpty: false,
            pipelineInjectedPaths: new[] { ".kiro" });

        // Assert
        unstaged.Should().Contain(f => f.StartsWith(".kiro/"));

        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".kiro"].Should().BeNull();
        tree["src/app.cs"].Should().NotBeNull();
    }

    [Fact]
    public void CommitAll_NullBlacklist_StillUnstagesOpenCodeDirectory()
    {
        // Arrange: OpenCode adds a .gitignore to its project config directory
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".opencode"));
        File.WriteAllText(Path.Combine(_workspacePath, ".opencode", ".gitignore"), "node_modules");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "app.cs"), "// code");

        // Act: pass .opencode as pipeline-injected path (from OpenCodeAgentProvider.PipelineInjectedPaths)
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: null, allowEmpty: false,
            pipelineInjectedPaths: new[] { ".opencode" });

        // Assert
        unstaged.Should().Contain(".opencode/.gitignore");

        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".opencode"].Should().BeNull();
        tree["src/app.cs"].Should().NotBeNull();
    }

    [Fact]
    public void CommitAll_AllHardcodedPaths_UnstagedRegardlessOfConfig()
    {
        // Arrange: all hardcoded paths present with arbitrary blacklist config
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".kiro", "steering"));
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".brain"));
        File.WriteAllText(Path.Combine(_workspacePath, ".agent", "mcp.json"), "{}");
        File.WriteAllText(Path.Combine(_workspacePath, ".kiro", "steering", "pipeline-project.md"), "project steering");
        File.WriteAllText(Path.Combine(_workspacePath, ".brain", "knowledge.md"), "brain content");
        File.WriteAllText(Path.Combine(_workspacePath, "CLAUDE.local.md"), "agent steering");
        File.WriteAllText(Path.Combine(_workspacePath, "src", "main.cs"), "// main");

        // Act: blacklist only contains unrelated paths, but provider injects .kiro + CLAUDE.local.md
        var unstaged = RepositoryGitOperations.CommitAll(_workspacePath, "test commit",
            blacklistedPaths: new[] { "node_modules", "dist" }, allowEmpty: false,
            pipelineInjectedPaths: new[] { ".kiro", "CLAUDE.local.md" });

        // Assert: all hardcoded paths unstaged (universal: .agent, .brain; provider: .kiro, CLAUDE.local.md)
        unstaged.Should().Contain(f => f.StartsWith(".agent/"));
        unstaged.Should().Contain(f => f.StartsWith(".kiro/"));
        unstaged.Should().Contain(f => f.StartsWith(".brain/"));
        unstaged.Should().Contain("CLAUDE.local.md");

        using var repo = new Repository(_workspacePath);
        var tree = repo.Head.Tip.Tree;
        tree[".agent"].Should().BeNull();
        tree[".kiro"].Should().BeNull();
        tree[".brain"].Should().BeNull();
        tree["CLAUDE.local.md"].Should().BeNull();
        tree["src/main.cs"].Should().NotBeNull();
    }

    private void InitGitRepo()
    {
        Repository.Init(_workspacePath);
        using var repo = new Repository(_workspacePath);
        var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);

        // Create src directory and initial file
        Directory.CreateDirectory(Path.Combine(_workspacePath, "src"));
        File.WriteAllText(Path.Combine(_workspacePath, "README.md"), "init\n");
        Commands.Stage(repo, "README.md");
        repo.Commit("initial", sig, sig, new CommitOptions());
    }
}
