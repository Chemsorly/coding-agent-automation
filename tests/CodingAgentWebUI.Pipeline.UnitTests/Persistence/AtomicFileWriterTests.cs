using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Persistence;

namespace CodingAgentWebUI.Pipeline.UnitTests.Persistence;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _testDir;

    public AtomicFileWriterTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AtomicFileWriterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_CreatesFileWithExpectedContent()
    {
        var targetPath = Path.Combine(_testDir, "output.json");
        var content = """{"key": "value", "number": 42}""";

        await AtomicFileWriter.WriteAsync(targetPath, content, CancellationToken.None);

        File.Exists(targetPath).Should().BeTrue();
        (await File.ReadAllTextAsync(targetPath)).Should().Be(content);
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFileAtomically()
    {
        var targetPath = Path.Combine(_testDir, "existing.json");
        var originalContent = """{"version": 1}""";
        var updatedContent = """{"version": 2, "extra": "data"}""";

        // Write original file
        await File.WriteAllTextAsync(targetPath, originalContent);
        File.Exists(targetPath).Should().BeTrue();

        // Overwrite atomically
        await AtomicFileWriter.WriteAsync(targetPath, updatedContent, CancellationToken.None);

        var result = await File.ReadAllTextAsync(targetPath);
        result.Should().Be(updatedContent);
    }

    [Fact]
    public async Task WriteAsync_WhenCancelled_DoesNotCorruptTargetFile()
    {
        var targetPath = Path.Combine(_testDir, "cancel-test.json");
        var originalContent = """{"preserved": true}""";

        // Write original content
        await File.WriteAllTextAsync(targetPath, originalContent);

        // Create a pre-cancelled token
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Attempt write with cancelled token — should throw
        var act = () => AtomicFileWriter.WriteAsync(targetPath, "corrupted data", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Original file must remain intact
        var result = await File.ReadAllTextAsync(targetPath);
        result.Should().Be(originalContent);
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectoryIfNotExists()
    {
        var nestedDir = Path.Combine(_testDir, "sub", "dir");
        var targetPath = Path.Combine(nestedDir, "nested.json");
        var content = "nested content";

        await AtomicFileWriter.WriteAsync(targetPath, content, CancellationToken.None);

        File.Exists(targetPath).Should().BeTrue();
        (await File.ReadAllTextAsync(targetPath)).Should().Be(content);
    }

    [Fact]
    public async Task WriteAsync_NoTmpFileRemainsAfterSuccess()
    {
        var targetPath = Path.Combine(_testDir, "clean.json");

        await AtomicFileWriter.WriteAsync(targetPath, "data", CancellationToken.None);

        var tmpFiles = Directory.GetFiles(_testDir, "*.tmp");
        tmpFiles.Should().BeEmpty();
    }
}
