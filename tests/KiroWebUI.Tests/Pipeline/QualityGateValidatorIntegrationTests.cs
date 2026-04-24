using AwesomeAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Tests.Helpers;
using Serilog;

namespace KiroWebUI.Tests.Pipeline;

[Trait("Category", "Integration")]
[Collection("QualityGateIntegration")]
public class QualityGateValidatorIntegrationTests : IDisposable
{
    private readonly QualityGateValidator _validator = new(Log.Logger);
    // TODO: Use TestPipelineConfig.Default() with MinCoverageThreshold override to stay in sync with production defaults
    private readonly PipelineConfiguration _config = new()
    {
        MinCoverageThreshold = 0,
        SecurityScanEnabled = false,
        MaxRetries = 3,
        IssuePageSize = 25,
        AgentTimeout = TimeSpan.FromMinutes(30),
        WorkspaceBaseDirectory = Path.GetTempPath()
    };
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// Copies a fixture project to a unique temp directory so parallel tests don't conflict
    /// on bin/obj locks, and returns the temp directory path.
    /// </summary>
    private string CopyFixtureToTemp(string fixtureName)
    {
        var fixtureSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        if (!Directory.Exists(fixtureSource))
            throw new DirectoryNotFoundException($"Fixture not found: {fixtureSource}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"qg-fixture-{Guid.NewGuid():N}");
        CopyDirectory(fixtureSource, tempDir);
        _tempDirs.Add(tempDir);
        return tempDir;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task PassingProject_ReturnsAllGatesPassed()
    {
        var workspace = CopyFixtureToTemp("PassingProject");

        var report = await _validator.ValidateAsync(workspace, _config, CancellationToken.None);

        report.Compilation.Passed.Should().BeTrue();
        report.Tests.Passed.Should().BeTrue();
        report.AllPassed.Should().BeTrue();
        report.Tests.TestsPassed.Should().BeGreaterThanOrEqualTo(1);
        report.Tests.TestsFailed.Should().Be(0);
    }

    [Fact]
    public async Task FailingBuildProject_ReportsCompilationFailure()
    {
        var workspace = CopyFixtureToTemp("FailingBuildProject");

        var report = await _validator.ValidateAsync(workspace, _config, CancellationToken.None);

        report.Compilation.Passed.Should().BeFalse();
        // TODO: Assert Compilation.Details contains actual error text (e.g., "CS0103" or "UndefinedType") for stronger regression coverage
        report.Compilation.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FailingTestProject_ReportsTestFailure()
    {
        var workspace = CopyFixtureToTemp("FailingTestProject");

        var report = await _validator.ValidateAsync(workspace, _config, CancellationToken.None);

        report.Compilation.Passed.Should().BeTrue();
        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsFailed.Should().BeGreaterThanOrEqualTo(1);
        report.Tests.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCancelled()
    {
        var workspace = CopyFixtureToTemp("PassingProject");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _validator.ValidateAsync(workspace, _config, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ShortTimeout_ThrowsOperationCancelled()
    {
        var workspace = CopyFixtureToTemp("PassingProject");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => _validator.ValidateAsync(workspace, _config, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NonExistentDirectory_ThrowsWin32Exception()
    {
        // TODO: Win32Exception is a Process.Start() implementation detail — consider asserting a broader exception type if this breaks across .NET versions
        var workspace = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

        var act = () => _validator.ValidateAsync(workspace, _config, CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
    }
}
