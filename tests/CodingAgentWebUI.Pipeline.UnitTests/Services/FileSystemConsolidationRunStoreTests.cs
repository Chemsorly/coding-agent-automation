using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using AwesomeAssertions;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for FileSystemConsolidationRunStore — filesystem-backed IConsolidationRunStore implementation.
/// Validates: save/load/delete/getById round-trip behavior using real temp directories.
/// </summary>
public sealed class FileSystemConsolidationRunStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemConsolidationRunStore _sut;

    public FileSystemConsolidationRunStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fs-run-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new FileSystemConsolidationRunStore(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static readonly string RunId1 = Guid.NewGuid().ToString();
    private static readonly string RunId2 = Guid.NewGuid().ToString();
    private static readonly string RunId3 = Guid.NewGuid().ToString();
    private static readonly string RunId4 = Guid.NewGuid().ToString();
    private static readonly string RunIdA = Guid.NewGuid().ToString();
    private static readonly string RunIdB = Guid.NewGuid().ToString();
    private static readonly string RunIdC = Guid.NewGuid().ToString();

    [Fact]
    public async Task SaveRunAsync_CreatesJsonFile_LoadAllReturnsIt()
    {
        var run = CreateRun(RunId1, ConsolidationRunType.BrainConsolidation);

        await _sut.SaveRunAsync(run, CancellationToken.None);

        var all = await _sut.LoadAllRunsAsync(CancellationToken.None);
        all.Should().ContainSingle(r => r.RunId == RunId1);
        all[0].Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        all[0].Status.Should().Be(ConsolidationRunStatus.Running);
    }

    [Fact]
    public async Task SaveRunAsync_ExistingRun_OverwritesFile()
    {
        var run = CreateRun(RunId2, ConsolidationRunType.RefactoringDetection);
        await _sut.SaveRunAsync(run, CancellationToken.None);

        run.Status = ConsolidationRunStatus.Succeeded;
        run.Summary = "Done";
        await _sut.SaveRunAsync(run, CancellationToken.None);

        var loaded = await _sut.GetByIdAsync(RunId2, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ConsolidationRunStatus.Succeeded);
        loaded.Summary.Should().Be("Done");
    }

    [Fact]
    public async Task GetByIdAsync_ExistingRun_ReturnsRun()
    {
        var run = CreateRun(RunId3, ConsolidationRunType.HarnessSuggestions);
        await _sut.SaveRunAsync(run, CancellationToken.None);

        var result = await _sut.GetByIdAsync(RunId3, CancellationToken.None);

        result.Should().NotBeNull();
        result!.RunId.Should().Be(RunId3);
        result.Type.Should().Be(ConsolidationRunType.HarnessSuggestions);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_InvalidId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync("not-a-guid", CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteRunAsync_ExistingRun_RemovesFile()
    {
        var run = CreateRun(RunId4, ConsolidationRunType.BrainConsolidation);
        await _sut.SaveRunAsync(run, CancellationToken.None);

        await _sut.DeleteRunAsync(RunId4, CancellationToken.None);

        var result = await _sut.GetByIdAsync(RunId4, CancellationToken.None);
        result.Should().BeNull();
        var all = await _sut.LoadAllRunsAsync(CancellationToken.None);
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRunAsync_NonExistent_DoesNotThrow()
    {
        var act = () => _sut.DeleteRunAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadAllRunsAsync_EmptyDirectory_ReturnsEmpty()
    {
        var all = await _sut.LoadAllRunsAsync(CancellationToken.None);
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllRunsAsync_DirectoryDoesNotExist_ReturnsEmpty()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");
        var store = new FileSystemConsolidationRunStore(nonExistentDir);

        var all = await store.LoadAllRunsAsync(CancellationToken.None);
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllRunsAsync_MultipleRuns_ReturnsAll()
    {
        await _sut.SaveRunAsync(CreateRun(RunIdA, ConsolidationRunType.BrainConsolidation), CancellationToken.None);
        await _sut.SaveRunAsync(CreateRun(RunIdB, ConsolidationRunType.RefactoringDetection), CancellationToken.None);
        await _sut.SaveRunAsync(CreateRun(RunIdC, ConsolidationRunType.HarnessSuggestions), CancellationToken.None);

        var all = await _sut.LoadAllRunsAsync(CancellationToken.None);
        all.Should().HaveCount(3);
    }

    private static ConsolidationRun CreateRun(string runId, ConsolidationRunType type) => new()
    {
        RunId = runId,
        Type = type,
        TemplateId = "tmpl-1",
        TemplateName = "Test Template",
        StartedAtUtc = DateTimeOffset.UtcNow,
        Status = ConsolidationRunStatus.Running
    };
}
