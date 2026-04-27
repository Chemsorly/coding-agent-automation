using KiroCliLib.Configuration;
using Serilog.Events;

namespace KiroCliLib.UnitTests.Configuration;

/// <summary>
/// Unit tests for ConfigurationManager.LoadAsync.
/// </summary>
public class ConfigurationManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KiroCliLib_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_FileDoesNotExist_ReturnsDefaults()
    {
        var config = await ConfigurationManager.LoadAsync("/nonexistent/path/config.json");

        Assert.Equal("/root/.local/bin/kiro-cli", config.KiroCliPath);
        Assert.Equal("./workspace", config.WorkspaceDirectory);
        Assert.Equal(TimeSpan.FromMinutes(30), config.Timeout);
    }

    [Fact]
    public async Task LoadAsync_NullPath_UsesDefaultPath_ReturnsDefaults()
    {
        // Default path is "config/appsettings.json" which likely doesn't exist in test context
        var config = await ConfigurationManager.LoadAsync(null);

        // Should return defaults without throwing
        Assert.NotNull(config);
    }

    [Fact]
    public async Task LoadAsync_ValidJson_ParsesCorrectly()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(configPath, """
        {
            "KiroCliPath": "/custom/kiro-cli",
            "UseWsl": false,
            "WorkspaceDirectory": "/my/workspace",
            "Timeout": "01:00:00",
            "LogLevel": "debug"
        }
        """);

        var config = await ConfigurationManager.LoadAsync(configPath);

        Assert.Equal("/custom/kiro-cli", config.KiroCliPath);
        Assert.False(config.UseWsl);
        Assert.Equal("/my/workspace", config.WorkspaceDirectory);
        Assert.Equal(TimeSpan.FromHours(1), config.Timeout);
        Assert.Equal(LogEventLevel.Debug, config.LogLevel);
    }

    [Fact]
    public async Task LoadAsync_EmptyJson_ReturnsDefaults()
    {
        var configPath = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(configPath, "{}");

        var config = await ConfigurationManager.LoadAsync(configPath);

        Assert.Equal("/root/.local/bin/kiro-cli", config.KiroCliPath);
        Assert.Equal("./workspace", config.WorkspaceDirectory);
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tempDir, "invalid.json");
        await File.WriteAllTextAsync(configPath, "not valid json {{{");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ConfigurationManager.LoadAsync(configPath));

        Assert.Contains("Failed to parse configuration file", ex.Message);
        Assert.Contains(configPath, ex.Message);
    }

    [Fact]
    public async Task LoadAsync_JsonWithComments_ParsesSuccessfully()
    {
        var configPath = Path.Combine(_tempDir, "commented.json");
        await File.WriteAllTextAsync(configPath, """
        {
            // This is a comment
            "KiroCliPath": "/with/comments",
            "WorkspaceDirectory": "/workspace"
        }
        """);

        var config = await ConfigurationManager.LoadAsync(configPath);

        Assert.Equal("/with/comments", config.KiroCliPath);
    }

    [Fact]
    public async Task LoadAsync_JsonWithTrailingCommas_ParsesSuccessfully()
    {
        var configPath = Path.Combine(_tempDir, "trailing.json");
        await File.WriteAllTextAsync(configPath, """
        {
            "KiroCliPath": "/trailing/comma",
            "WorkspaceDirectory": "/workspace",
        }
        """);

        var config = await ConfigurationManager.LoadAsync(configPath);

        Assert.Equal("/trailing/comma", config.KiroCliPath);
    }

    [Fact]
    public async Task LoadAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(configPath, """{ "KiroCliPath": "/test" }""");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConfigurationManager.LoadAsync(configPath, cts.Token));
    }

    [Fact]
    public async Task LoadAsync_PartialConfig_MergesWithDefaults()
    {
        var configPath = Path.Combine(_tempDir, "partial.json");
        await File.WriteAllTextAsync(configPath, """
        {
            "KiroCliPath": "/custom/path"
        }
        """);

        var config = await ConfigurationManager.LoadAsync(configPath);

        Assert.Equal("/custom/path", config.KiroCliPath);
        // Other properties should have defaults
        Assert.Equal("./workspace", config.WorkspaceDirectory);
        Assert.Equal(TimeSpan.FromMinutes(30), config.Timeout);
    }
}
