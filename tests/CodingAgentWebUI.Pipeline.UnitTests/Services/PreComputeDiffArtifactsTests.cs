using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Integration tests for <see cref="AgentPhaseExecutor.PreComputeDiffArtifactsAsync"/>
/// using real temporary git repositories.
/// </summary>
[Trait("Category", "Integration")]
public class PreComputeDiffArtifactsTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly Serilog.ILogger _logger = Serilog.Core.Logger.None;

    public PreComputeDiffArtifactsTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"diff-artifacts-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        InitGitRepo();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    [Fact]
    public async Task UntrackedFiles_AppearInDiffStat()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "newfile.txt"), "hello world\n");

        await RunPreComputeAsync();

        var diffStat = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.DiffStatFilePath));
        diffStat.Should().Contain("newfile.txt");
    }

    [Fact]
    public async Task UntrackedFiles_AppearInFullDiffAsNewFile()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "newfile.txt"), "hello world\n");

        await RunPreComputeAsync();

        var fullDiff = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.FullDiffFilePath));
        fullDiff.Should().Contain("+++ b/newfile.txt");
        fullDiff.Should().Contain("+hello world");
    }

    [Fact]
    public async Task GitIndex_IsCleanAfterCompletion()
    {
        File.WriteAllText(Path.Combine(_workspacePath, "newfile.txt"), "hello\n");

        await RunPreComputeAsync();

        // git status --porcelain should show ?? (untracked), not A (staged/ITA)
        var status = await RunGitAsync("status --porcelain");
        status.Should().Contain("?? newfile.txt");
        status.Should().NotContain("A  newfile.txt");
    }

    [Fact]
    public async Task GitignorePatterns_AreRespected()
    {
        File.WriteAllText(Path.Combine(_workspacePath, ".gitignore"), "*.log\n");
        await RunGitAsync("add .gitignore");
        await RunGitAsync("commit -m \"add gitignore\"");
        File.WriteAllText(Path.Combine(_workspacePath, "debug.log"), "should be ignored\n");

        await RunPreComputeAsync();

        var diffStat = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.DiffStatFilePath));
        diffStat.Should().NotContain("debug.log");
    }

    [Fact]
    public async Task BinaryFiles_DoNotCrash()
    {
        File.WriteAllBytes(Path.Combine(_workspacePath, "image.bin"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03 });

        await RunPreComputeAsync();

        var diffStat = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.DiffStatFilePath));
        diffStat.Should().Contain("image.bin");
        var fullDiff = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.FullDiffFilePath));
        fullDiff.Should().Contain("Binary files");
    }

    [Fact]
    public async Task TrackedFileModifications_StillWork()
    {
        // Create and commit a tracked file
        File.WriteAllText(Path.Combine(_workspacePath, "existing.txt"), "original\n");
        await RunGitAsync("add existing.txt");
        await RunGitAsync("commit -m \"add existing\"");

        // Modify it
        File.WriteAllText(Path.Combine(_workspacePath, "existing.txt"), "modified\n");

        await RunPreComputeAsync();

        var diffStat = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.DiffStatFilePath));
        diffStat.Should().Contain("existing.txt");
        var fullDiff = await File.ReadAllTextAsync(Path.Combine(_workspacePath, AgentWorkspacePaths.FullDiffFilePath));
        fullDiff.Should().Contain("+modified");
    }

    private async Task RunPreComputeAsync()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            WorkspacePath = _workspacePath
        };

        await AgentPhaseExecutor.PreComputeDiffArtifactsAsync(run, _logger, CancellationToken.None);
    }

    private void InitGitRepo()
    {
        RunGitSync("init");
        RunGitSync("config user.email \"test@test.com\"");
        RunGitSync("config user.name \"Test\"");
        // Create initial commit so origin/main ref can exist
        File.WriteAllText(Path.Combine(_workspacePath, "README.md"), "init\n");
        RunGitSync("add .");
        RunGitSync("commit -m \"initial\"");
        // Create a local ref that mimics origin/main pointing at HEAD
        RunGitSync("update-ref refs/remotes/origin/main HEAD");
    }

    private void RunGitSync(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {p.StandardError.ReadToEnd()}");
    }

    private async Task<string> RunGitAsync(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _workspacePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
