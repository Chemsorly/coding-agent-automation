using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class QualityGateQuarantineTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly TestableValidator _validator = new();

    public QualityGateQuarantineTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"qg-quarantine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, true);
    }

    [Fact]
    public async Task AllFailuresQuarantined_GatePasses()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.FlakyTest1", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) },
                new QuarantinedTest { TestName = "Ns.FlakyTest2", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest1", "Ns.FlakyTest2"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeTrue();
        report.Tests.TestsFailed.Should().Be(0);
        report.Tests.TestsQuarantined.Should().Be(2);
        report.Tests.QuarantinedTestNames.Should().Contain("Ns.FlakyTest1");
        report.Tests.QuarantinedTestNames.Should().Contain("Ns.FlakyTest2");
    }

    [Fact]
    public async Task MixedQuarantinedAndReal_GateFails()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.FlakyTest", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest", "Ns.RealFailure"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsFailed.Should().Be(1);
        report.Tests.TestsQuarantined.Should().Be(1);
    }

    [Fact]
    public async Task QuarantinedTestSourceModified_QuarantineLifted()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest
                {
                    TestName = "Ns.FlakyTest",
                    Reason = "flaky",
                    QuarantinedAt = DateTime.UtcNow.AddDays(-1),
                    AssociatedSourceFiles = ["src/MyService.cs"]
                }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest"]);
        var modifiedFiles = new List<string> { "src/MyService.cs" };

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], modifiedFiles, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsFailed.Should().Be(1);
        report.Tests.TestsQuarantined.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredQuarantineEntry_TreatedAsNonQuarantined()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest
                {
                    TestName = "Ns.FlakyTest",
                    Reason = "flaky",
                    QuarantinedAt = DateTime.UtcNow.AddDays(-60),
                    ExpiresAt = DateTime.UtcNow.AddDays(-1) // expired yesterday
                }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsQuarantined.Should().BeNull();
    }

    [Fact]
    public async Task ExceedsMaxQuarantinedFailures_GateFails()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            MaxQuarantinedFailuresPerRun = 2,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.Flaky1", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) },
                new QuarantinedTest { TestName = "Ns.Flaky2", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) },
                new QuarantinedTest { TestName = "Ns.Flaky3", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
            ]
        });

        _validator.SetupFailingTests(["Ns.Flaky1", "Ns.Flaky2", "Ns.Flaky3"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsQuarantined.Should().BeNull(); // safety valve triggered, no quarantine applied
    }

    [Fact]
    public async Task QuarantineDisabled_NormalBehavior()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = false,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.FlakyTest", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsQuarantined.Should().BeNull();
    }

    [Fact]
    public async Task NonDotnetQgc_QuarantineNotApplied()
    {
        var qgc = new QualityGateConfiguration
        {
            DisplayName = "Python Tests",
            TestCommand = "pytest",
            TestArguments = ["tests/"],
            TestQuarantine = new TestQuarantineConfiguration
            {
                Enabled = true,
                QuarantinedTests = [
                    new QuarantinedTest { TestName = "test_flaky", Reason = "flaky", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
                ]
            }
        };

        // Non-dotnet: stdout parsing, no TRX, no individual test names → quarantine can't apply
        _validator.SetupNonDotnetFailure("1 failed, 5 passed in 2.3s");

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.Passed.Should().BeFalse();
        report.Tests.TestsQuarantined.Should().BeNull();
    }

    [Fact]
    public async Task QuarantinedTestNamesReportedOnGateResult()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest { TestName = "Ns.FlakyTest", Reason = "timing issue", QuarantinedAt = DateTime.UtcNow.AddDays(-1) }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest"]);

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], null, CancellationToken.None);

        report.Tests.QuarantinedTestNames.Should().ContainSingle().Which.Should().Be("Ns.FlakyTest");
        report.Tests.Details.Should().Contain("quarantined");
    }

    [Fact]
    public async Task QuarantinedTest_NoAssociatedSourceFiles_AlwaysQuarantined()
    {
        var qgc = CreateQgc(quarantine: new TestQuarantineConfiguration
        {
            Enabled = true,
            QuarantinedTests = [
                new QuarantinedTest
                {
                    TestName = "Ns.FlakyTest",
                    Reason = "flaky",
                    QuarantinedAt = DateTime.UtcNow.AddDays(-1),
                    AssociatedSourceFiles = null // no source files → always quarantined
                }
            ]
        });

        _validator.SetupFailingTests(["Ns.FlakyTest"]);
        var modifiedFiles = new List<string> { "src/SomeOtherFile.cs" };

        var report = await _validator.ValidateAsync(_workspacePath, [qgc], modifiedFiles, CancellationToken.None);

        report.Tests.Passed.Should().BeTrue();
        report.Tests.TestsQuarantined.Should().Be(1);
    }

    private static QualityGateConfiguration CreateQgc(TestQuarantineConfiguration? quarantine = null) => new()
    {
        DisplayName = "Test QGC",
        CompilationCommand = "dotnet",
        CompilationArguments = ["build"],
        TestCommand = "dotnet",
        TestArguments = ["test"],
        TestQuarantine = quarantine
    };

    /// <summary>
    /// Testable subclass that overrides RunProcessAsync to simulate test results
    /// by writing TRX files and returning appropriate exit codes.
    /// </summary>
    private sealed class TestableValidator : QualityGateValidator
    {
        private List<string> _failingTests = [];
        private string? _nonDotnetStdout;

        public TestableValidator() : base(Log.Logger) { }

        public void SetupFailingTests(List<string> failingTests)
        {
            _failingTests = failingTests;
            _nonDotnetStdout = null;
        }

        public void SetupNonDotnetFailure(string stdout)
        {
            _nonDotnetStdout = stdout;
            _failingTests = [];
        }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct)
        {
            // Compilation always passes
            if (arguments.Contains("build"))
                return Task.FromResult((0, "Build succeeded.", ""));

            // Non-dotnet test command
            if (_nonDotnetStdout != null)
                return Task.FromResult((1, $"======= {_nonDotnetStdout} =======", ""));

            // .NET test: write TRX file to the results directory
            var resultsDirMatch = System.Text.RegularExpressions.Regex.Match(arguments, @"--results-directory ""([^""]+)""");
            if (resultsDirMatch.Success)
            {
                var resultsDir = resultsDirMatch.Groups[1].Value;
                Directory.CreateDirectory(resultsDir);
                WriteTrxWithFailures(resultsDir, _failingTests);
            }

            var exitCode = _failingTests.Count > 0 ? 1 : 0;
            return Task.FromResult((exitCode, "", ""));
        }

        private static void WriteTrxWithFailures(string dir, List<string> failingTests)
        {
            var passed = 10;
            var failed = failingTests.Count;
            var total = passed + failed;

            var results = string.Join("\n", failingTests.Select(t =>
                $"    <UnitTestResult testName=\"{t}\" outcome=\"Failed\" />"));
            for (var i = 0; i < passed; i++)
                results += $"\n    <UnitTestResult testName=\"PassingTest{i}\" outcome=\"Passed\" />";

            var xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <ResultSummary outcome="Completed">
                    <Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" error="0" notExecuted="0" />
                  </ResultSummary>
                  <Results>
                {results}
                  </Results>
                </TestRun>
                """;
            File.WriteAllText(Path.Combine(dir, "results.trx"), xml);
        }
    }
}
