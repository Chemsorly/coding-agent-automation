using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.IntegrationTests.Helpers;
using Serilog;

namespace CodingAgentWebUI.IntegrationTests.Pipeline;

[Trait("Category", "Integration")]
[Collection("QualityGateIntegration")]
public class QualityGateValidatorIntegrationTests : IDisposable
{
    private readonly QualityGateValidator _validator = new(Log.Logger);
    private static readonly IReadOnlyList<QualityGateConfiguration> DefaultQgcs = new[]
    {
        new QualityGateConfiguration
        {
            Id = "test-default",
            DisplayName = "Default",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build", "--no-restore"],
            TestCommand = "dotnet",
            TestArguments = ["test", "--no-restore", "--no-build"],
            CoverageThreshold = 0,
            SecurityScanEnabled = false,
            Enabled = true,
            ExecutionOrder = 0
        }
    };
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// Copies a fixture project to a unique temp directory so parallel tests don't conflict
    /// on bin/obj locks, restores NuGet packages, and returns the temp directory path.
    /// </summary>
    private string CopyFixtureToTemp(string fixtureName)
    {
        var fixtureSource = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        if (!Directory.Exists(fixtureSource))
            throw new DirectoryNotFoundException($"Fixture not found: {fixtureSource}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"qg-fixture-{Guid.NewGuid():N}");
        CopyDirectory(fixtureSource, tempDir);
        _tempDirs.Add(tempDir);

        // Restore NuGet packages so --no-restore builds work
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(60_000);

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

        var report = await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        report.Compilation.Passed.Should().BeTrue();
        report.Compilation.Details.Should().Be("All QGC compilations passed");
        report.Tests.Passed.Should().BeTrue();
        report.AllPassed.Should().BeTrue();
        // Per-QGC result has the detailed test counts
        var qgcResult = report.QgcResults[0];
        qgcResult.Tests!.TestsPassed.Should().BeGreaterThanOrEqualTo(1);
        qgcResult.Tests.TestsFailed.Should().Be(0);
    }

    [Fact]
    public async Task FailingBuildProject_ReportsCompilationFailure()
    {
        var workspace = CopyFixtureToTemp("FailingBuildProject");

        var report = await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        report.Compilation.Passed.Should().BeFalse();
        report.Compilation.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FailingBuildProject_WritesOutputToQualityGatesDirectory()
    {
        var workspace = CopyFixtureToTemp("FailingBuildProject");

        await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        var gatesDir = Path.Combine(workspace, PromptBuilder.QualityGatesOutputDirectory);
        Directory.Exists(gatesDir).Should().BeTrue();
        File.Exists(Path.Combine(gatesDir, "Default-compilation-stdout.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task FailingBuildProject_DetailsContainsSummaryOnly()
    {
        var workspace = CopyFixtureToTemp("FailingBuildProject");

        var report = await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        // Aggregate details should indicate which QGC failed
        report.Compilation.Details.Should().Contain("Compilation failed in QGC");
        // Per-QGC result should have the detailed build failure info
        report.QgcResults.Should().NotBeEmpty();
        var qgcResult = report.QgcResults[0];
        qgcResult.Compilation!.Details.Should().Contain("Build failed with exit code");
        qgcResult.Compilation.Details.Should().Contain("error(s)");
        qgcResult.Compilation.Details.Should().NotContain("error CS");
    }

    [Fact]
    public async Task FailingTestProject_ReportsTestFailure()
    {
        var workspace = CopyFixtureToTemp("FailingTestProject");

        var report = await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        report.Compilation.Passed.Should().BeTrue();
        report.Tests.Passed.Should().BeFalse();
        // Per-QGC result has the detailed test counts
        var qgcResult = report.QgcResults[0];
        qgcResult.Tests!.TestsFailed.Should().BeGreaterThanOrEqualTo(1);
        qgcResult.Tests.Details.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FailingTestProject_WritesTestOutputToQualityGatesDirectory()
    {
        var workspace = CopyFixtureToTemp("FailingTestProject");

        await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        var gatesDir = Path.Combine(workspace, PromptBuilder.QualityGatesOutputDirectory);
        Directory.Exists(gatesDir).Should().BeTrue();
        // Test output is written as stdout (stderr may or may not be present depending on test runner version)
        File.Exists(Path.Combine(gatesDir, "Default-tests-stdout.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task FailingTestProject_DetailsDoesNotContainRawStderr()
    {
        var workspace = CopyFixtureToTemp("FailingTestProject");

        var report = await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        // Aggregate compilation should pass (only tests fail)
        report.Compilation.Passed.Should().BeTrue();
        report.Compilation.Details.Should().Be("All QGC compilations passed");
        // Aggregate tests should report failure
        report.Tests.Passed.Should().BeFalse();
        report.Tests.Details.Should().Contain("Tests failed in QGC");
        // Per-QGC test details should have the counts
        var qgcResult = report.QgcResults[0];
        qgcResult.Tests!.Details.Should().StartWith("Tests failed:");
        qgcResult.Tests.Details.Should().EndWith("skipped.");
    }

    [Fact]
    public async Task QualityGatesDirectory_ClearedOnEachRun()
    {
        var workspace = CopyFixtureToTemp("FailingBuildProject");
        var gatesDir = Path.Combine(workspace, PromptBuilder.QualityGatesOutputDirectory);

        // Create a stale file that should be cleaned up
        Directory.CreateDirectory(gatesDir);
        File.WriteAllText(Path.Combine(gatesDir, "stale-file.txt"), "old data");

        await _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        File.Exists(Path.Combine(gatesDir, "stale-file.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCancelled()
    {
        var workspace = CopyFixtureToTemp("PassingProject");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _validator.ValidateAsync(workspace, DefaultQgcs, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ShortTimeout_ThrowsOperationCancelled()
    {
        var workspace = CopyFixtureToTemp("PassingProject");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => _validator.ValidateAsync(workspace, DefaultQgcs, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NonExistentDirectory_ThrowsWin32Exception()
    {
        // TODO: Win32Exception is a Process.Start() implementation detail — consider asserting a broader exception type if this breaks across .NET versions
        var workspace = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

        var act = () => _validator.ValidateAsync(workspace, DefaultQgcs, CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.Win32Exception>();
    }
}
