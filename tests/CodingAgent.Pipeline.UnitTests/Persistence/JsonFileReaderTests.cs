using AwesomeAssertions;
using CodingAgent.Pipeline.Persistence;
using Moq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgent.Pipeline.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="JsonFileReader.TryReadJsonFileAsync{T}"/>.
/// Covers all documented behavior: missing file, empty file, whitespace file,
/// malformed JSON, valid JSON, null deserialization result, IOException, and
/// cancellation propagation.
/// </summary>
public sealed class JsonFileReaderTests : IDisposable
{
    private readonly string _testDir;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private static readonly JsonSerializerOptions DefaultOptions = new();

    public JsonFileReaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"json-file-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _mockLogger = new Mock<Serilog.ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(_testDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private string NonExistentPath() =>
        Path.Combine(_testDir, $"{Guid.NewGuid():N}-missing.json");

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TryReadJsonFileAsync_MissingFile_ReturnsNull()
    {
        var path = NonExistentPath();

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
        // Missing file logs a Debug message
        _mockLogger.Verify(
            l => l.Debug(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task TryReadJsonFileAsync_EmptyFile_ReturnsNull_NoWarningLogged()
    {
        // Empty file is a silent null — not a parse failure, so no Warning should be emitted
        var path = WriteTempFile("");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task TryReadJsonFileAsync_WhitespaceOnlyFile_ReturnsNull_NoWarningLogged()
    {
        // Whitespace-only content is treated the same as empty — silent null, no Warning
        var path = WriteTempFile("   \n\t  ");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task TryReadJsonFileAsync_MalformedJson_ReturnsNull_LogsWarning()
    {
        var path = WriteTempFile("{ this is not valid json !!!");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task TryReadJsonFileAsync_ValidJson_ReturnsDeserializedValue()
    {
        var path = WriteTempFile("""{"Name":"hello","Value":42}""");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Name.Should().Be("hello");
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task TryReadJsonFileAsync_NullDeserializationResult_ReturnsNull()
    {
        // A JSON "null" literal deserializes to null for reference types
        var path = WriteTempFile("null");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryReadJsonFileAsync_CancellationRequested_Propagates()
    {
        // OperationCanceledException must not be swallowed
        var path = WriteTempFile("""{"Name":"x","Value":1}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, DefaultOptions, _mockLogger.Object, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryReadJsonFileAsync_NonJsonExceptionDuringRead_ReturnsNull_LogsWarning()
    {
        // Verifies that non-JsonException exceptions (e.g. IOException) are also caught
        // and trigger the Warning log — not just JsonException.
        // We use a custom JsonConverter that throws IOException during deserialization
        // to exercise the catch-all Exception path in the helper.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ThrowingIoExceptionConverter());
        var path = WriteTempFile("""{"Name":"test","Value":1}""");

        var result = await JsonFileReader.TryReadJsonFileAsync<SimpleDto>(
            path, options, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // ── Supporting types ───────────────────────────────────────────────

    private sealed record SimpleDto(string Name, int Value);

    /// <summary>
    /// A JsonConverter that throws <see cref="IOException"/> unconditionally,
    /// used to confirm that exceptions other than <see cref="JsonException"/>
    /// are also caught and warned by <see cref="JsonFileReader.TryReadJsonFileAsync{T}"/>.
    /// </summary>
    private sealed class ThrowingIoExceptionConverter : JsonConverter<SimpleDto>
    {
        public override SimpleDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new IOException("Simulated I/O failure during deserialization");

        public override void Write(Utf8JsonWriter writer, SimpleDto value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
