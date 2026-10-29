namespace CodingAgent.Pipeline.UnitTests.Services;

using AwesomeAssertions;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Xunit;

/// <summary>
/// Tests for <see cref="BrainSkillsReader.ReadAsync"/> (spec 054, req 5).
/// Uses temp directories — no Docker (T12).
/// </summary>
public sealed class BrainSkillsReaderTests : IDisposable
{
    private readonly string _brainPath;

    public BrainSkillsReaderTests()
    {
        _brainPath = Path.Combine(Path.GetTempPath(), $"brain-skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_brainPath);
    }

    private static ILogger NullLogger() => new LoggerConfiguration().CreateLogger();

    private void WriteSkill(string relativePath, string content)
    {
        var full = Path.Combine(_brainPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static Mock<IGitWorkspaceProvider> ProviderWith(string sha)
    {
        var m = new Mock<IGitWorkspaceProvider>();
        m.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(sha);
        return m;
    }

    [Fact]
    public async Task ReadAsync_EmptyBrain_ReturnsEmptyInjection()
    {
        var provider = ProviderWith("abc1234");
        var result = await BrainSkillsReader.ReadAsync(
            _brainPath, "org/repo", [], provider.Object, NullLogger(), CancellationToken.None);

        result.Text.Should().BeEmpty();
        result.Result.Should().Be(BrainSkillsResults.Empty);
        result.Commit.Should().Be("abc1234");
    }

    [Fact]
    public async Task ReadAsync_GeneralSkillExists_ReturnsInjectedResult()
    {
        WriteSkill(BrainLayout.GeneralSkillPath,
            $"{BrainLayout.SkillMarker}\n- use parameterized queries\n");

        var provider = ProviderWith("deadbeef");
        var result = await BrainSkillsReader.ReadAsync(
            _brainPath, null, [], provider.Object, NullLogger(), CancellationToken.None);

        result.Result.Should().Be(BrainSkillsResults.Injected);
        result.Text.Should().Contain("use parameterized queries");
        result.Bytes.Should().BeGreaterThan(0);
        result.Commit.Should().Be("deadbeef");
    }

    [Fact]
    public async Task ReadAsync_RepositorySkillExists_IncludesItInInjection()
    {
        WriteSkill("projects/my-repo/SKILL.md",
            $"{BrainLayout.SkillMarker}\n- repository-specific tip\n");

        var provider = ProviderWith("abc123");
        var result = await BrainSkillsReader.ReadAsync(
            _brainPath, "my-repo", [], provider.Object, NullLogger(), CancellationToken.None);

        result.Result.Should().Be(BrainSkillsResults.Injected);
        result.Text.Should().Contain("repository-specific tip");
        result.FilesInjected.Should().Contain(f => f.Contains("my-repo"));
    }

    [Fact]
    public async Task ReadAsync_GetHeadCommitShaThrows_ReturnsNullCommitSha()
    {
        // The inner exception branch for GetHeadCommitShaAsync
        var provider = new Mock<IGitWorkspaceProvider>();
        provider.Setup(p => p.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("no repo"));

        var result = await BrainSkillsReader.ReadAsync(
            _brainPath, null, [], provider.Object, NullLogger(), CancellationToken.None);

        result.Commit.Should().BeNull("GetHeadCommitShaAsync exception must be caught non-fatally (T6)");
        result.Result.Should().NotBe(BrainSkillsResults.NoBrain);
    }

    [Fact]
    public async Task ReadAsync_SkillFileOverBudget_SkipsIt()
    {
        // Write a file that is larger than MaxSkillFileBytes
        var bigContent = $"{BrainLayout.SkillMarker}\n{new string('-', BrainLayout.MaxSkillFileBytes + 100)}\n";
        WriteSkill(BrainLayout.GeneralSkillPath, bigContent);

        var provider = ProviderWith("sha1");
        var result = await BrainSkillsReader.ReadAsync(
            _brainPath, null, [], provider.Object, NullLogger(), CancellationToken.None);

        // The over-budget file is passed as empty → assembler skips it
        result.Result.Should().NotBe(BrainSkillsResults.NoBrain);
    }

    [Fact]
    public async Task ReadAsync_OuterExceptionPath_ReturnsNoBrainResult()
    {
        // Make the brainPath a file, not a directory, so Directory operations fail
        var badPath = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.txt");
        File.WriteAllText(badPath, "not a directory");

        try
        {
            var provider = ProviderWith("x");
            var result = await BrainSkillsReader.ReadAsync(
                badPath, "repo", [], provider.Object, NullLogger(), CancellationToken.None);

            // Should not throw — returns NoBrain fallback (T6)
            result.Result.Should().Be(BrainSkillsResults.NoBrain);
        }
        finally
        {
            File.Delete(badPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_brainPath))
        {
            foreach (var f in Directory.EnumerateFiles(_brainPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_brainPath, recursive: true);
        }
    }
}
