using KiroCliLib.Configuration;
using Serilog.Events;

namespace KiroCliLib.UnitTests.Configuration;

/// <summary>
/// Unit tests for Configuration defaults.
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void DefaultConfiguration_HasExpectedDefaults()
    {
        var config = new KiroCliLib.Configuration.Configuration();

        Assert.Equal("/root/.local/bin/kiro-cli", config.KiroCliPath);
        Assert.Equal("./workspace", config.WorkspaceDirectory);
        Assert.Equal(TimeSpan.FromMinutes(30), config.Timeout);
        Assert.Equal(LogEventLevel.Information, config.LogLevel);
    }

    [Fact]
    public void Configuration_UseWsl_DefaultsBasedOnPlatform()
    {
        var config = new KiroCliLib.Configuration.Configuration();

        // UseWsl defaults to true on Windows, false on Linux/Mac
        Assert.Equal(OperatingSystem.IsWindows(), config.UseWsl);
    }

    [Fact]
    public void Configuration_InitProperties_CanBeSet()
    {
        var config = new KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = "/custom/path",
            UseWsl = false,
            WorkspaceDirectory = "/my/workspace",
            Timeout = TimeSpan.FromMinutes(60),
            LogLevel = LogEventLevel.Debug
        };

        Assert.Equal("/custom/path", config.KiroCliPath);
        Assert.False(config.UseWsl);
        Assert.Equal("/my/workspace", config.WorkspaceDirectory);
        Assert.Equal(TimeSpan.FromMinutes(60), config.Timeout);
        Assert.Equal(LogEventLevel.Debug, config.LogLevel);
    }
}
