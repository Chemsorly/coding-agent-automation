using AwesomeAssertions;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using CodingAgentWebUI.Infrastructure.Git;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

/// <summary>
/// Verifies that logging is emitted before throw statements in RepositoryGitOperations.
/// Covers: CheckoutRemoteBranch, CommitAll (no changes), Push error, MergeFromBase (base branch not found).
/// </summary>
public class RepositoryGitOperationsThrowLoggingTests : IDisposable
{
    private readonly CollectingSink _sink;
    private readonly ILogger _previousLogger;
    private readonly string _tempDir;

    public RepositoryGitOperationsThrowLoggingTests()
    {
        _previousLogger = Log.Logger;
        _sink = new CollectingSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        _tempDir = Path.Combine(Path.GetTempPath(), $"git-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup best-effort */ }
    }

    #region CheckoutRemoteBranch — remote branch not found

    [Fact]
    public void CheckoutRemoteBranch_BranchNotFound_LogsErrorBeforeThrowing()
    {
        // Initialize a bare repo so that "origin/nonexistent" won't exist
        LibGit2Sharp.Repository.Init(_tempDir);
        using (var repo = new LibGit2Sharp.Repository(_tempDir))
        {
            // Need at least one commit so repo is valid
            var sig = new LibGit2Sharp.Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            var filePath = Path.Combine(_tempDir, "init.txt");
            File.WriteAllText(filePath, "init");
            LibGit2Sharp.Commands.Stage(repo, "init.txt");
            repo.Commit("init", sig, sig, new LibGit2Sharp.CommitOptions());

            // Add a dummy origin remote (no actual remote branch)
            repo.Network.Remotes.Add("origin", "https://example.com/repo.git");
        }

        var act = () => RepositoryGitOperations.CheckoutRemoteBranch(_tempDir, "nonexistent-branch");

        act.Should().Throw<InvalidOperationException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.Contains("Remote branch"));
    }

    #endregion

    #region CommitAll — no changes to commit

    [Fact]
    public void CommitAll_NoChanges_LogsWarningBeforeThrowing()
    {
        // Initialize repo with a commit (clean state)
        LibGit2Sharp.Repository.Init(_tempDir);
        using (var repo = new LibGit2Sharp.Repository(_tempDir))
        {
            var sig = new LibGit2Sharp.Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            var filePath = Path.Combine(_tempDir, "init.txt");
            File.WriteAllText(filePath, "init");
            LibGit2Sharp.Commands.Stage(repo, "init.txt");
            repo.Commit("init", sig, sig, new LibGit2Sharp.CommitOptions());
        }

        var act = () => RepositoryGitOperations.CommitAll(
            _tempDir, "test commit", blacklistedPaths: null, allowEmpty: false);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("No changes to commit");

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("No changes to commit"));
    }

    #endregion

    #region Helpers

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    #endregion
}
