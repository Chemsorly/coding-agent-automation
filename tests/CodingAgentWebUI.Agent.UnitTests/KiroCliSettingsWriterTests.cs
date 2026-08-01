using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="KiroCliSettingsWriter.ApplyAsync"/>.
/// Requirements: Req 16 — chat pods apply model and effort to cli.json on startup.
/// </summary>
public class KiroCliSettingsWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cliJsonPath;

    public KiroCliSettingsWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kiro-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _cliJsonPath = Path.Combine(_tempDir, "cli.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─── Model + effort → correct JSON written ────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WithModelAndEffort_WritesCorrectJsonToCLiJson()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "claude-opus-4.8", "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeTrue("cli.json must be created");

        var json = await File.ReadAllTextAsync(_cliJsonPath);
        var root = JsonNode.Parse(json)!.AsObject();

        root["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-opus-4.8");

        var modelDefaults = root["chat.modelDefaults"]!.AsObject();
        var effortValue = modelDefaults["claude-opus-4.8"]!["output_config"]!["effort"]!.GetValue<string>();
        effortValue.Should().Be("high");
    }

    [Fact]
    public async Task ApplyAsync_WithModelOnly_NoEffort_WritesDefaultModelButNoEffort()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "claude-sonnet-4", null, CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(_cliJsonPath);
        var root = JsonNode.Parse(json)!.AsObject();

        root["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-sonnet-4");
        root.ContainsKey("chat.modelDefaults").Should().BeFalse(
            "no chat.modelDefaults node when effort is not provided");
    }

    // ─── "auto" model → no file write ────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WithAutoModel_DoesNotWriteFile()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "auto", "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeFalse(
            "'auto' model must skip the file write entirely");
    }

    [Fact]
    public async Task ApplyAsync_WithAutoModelUppercase_DoesNotWriteFile()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "AUTO", "low", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeFalse("case-insensitive 'auto' comparison");
    }

    // ─── null/empty model → no file write ────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WithNullModel_DoesNotWriteFile()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            null!, "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeFalse("null model must skip write");
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyModel_DoesNotWriteFile()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            string.Empty, "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        File.Exists(_cliJsonPath).Should().BeFalse("empty model must skip write");
    }

    // ─── Called twice → merges correctly ─────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_CalledTwice_SecondCallPreservesFirstCallData()
    {
        // First call: model A with effort
        await KiroCliSettingsWriter.ApplyAsync(
            "claude-opus-4.8", "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        // Second call: model B with different effort
        await KiroCliSettingsWriter.ApplyAsync(
            "claude-sonnet-4", "low", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        var json = await File.ReadAllTextAsync(_cliJsonPath);
        var root = JsonNode.Parse(json)!.AsObject();

        // Second call's defaultModel wins
        root["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-sonnet-4");

        // First call's modelDefaults entry must still be present
        var modelDefaults = root["chat.modelDefaults"]!.AsObject();
        modelDefaults.ContainsKey("claude-opus-4.8").Should().BeTrue(
            "first call's model entry must be preserved on second call");
        modelDefaults.ContainsKey("claude-sonnet-4").Should().BeTrue(
            "second call's model entry must be present");

        modelDefaults["claude-opus-4.8"]!["output_config"]!["effort"]!.GetValue<string>()
            .Should().Be("high", "first model's effort unchanged by second call");

        modelDefaults["claude-sonnet-4"]!["output_config"]!["effort"]!.GetValue<string>()
            .Should().Be("low", "second model's effort written correctly");
    }

    [Fact]
    public async Task ApplyAsync_CalledTwice_SameModel_OverwritesEffort()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "claude-opus-4.8", "high", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        await KiroCliSettingsWriter.ApplyAsync(
            "claude-opus-4.8", "low", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        var json = await File.ReadAllTextAsync(_cliJsonPath);
        var root = JsonNode.Parse(json)!.AsObject();

        var effort = root["chat.modelDefaults"]!["claude-opus-4.8"]!["output_config"]!["effort"]!
            .GetValue<string>();
        effort.Should().Be("low", "same model second call must overwrite effort");
    }

    // ─── File is valid JSON after write ──────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_WritesValidJson()
    {
        await KiroCliSettingsWriter.ApplyAsync(
            "test-model", "medium", CancellationToken.None,
            settingsPathOverride: _cliJsonPath);

        var json = await File.ReadAllTextAsync(_cliJsonPath);

        // Must not throw
        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow("output must be valid JSON");
    }
}
