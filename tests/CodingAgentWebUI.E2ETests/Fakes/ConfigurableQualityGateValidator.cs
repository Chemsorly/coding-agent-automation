using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// Quality gate validator with configurable pass/fail behavior.
/// Supports attempt-based behavior for retry tests.
/// </summary>
public sealed class ConfigurableQualityGateValidator : IQualityGateValidator
{
    private int _attemptCount;

    /// <summary>
    /// When set, this function is called with the attempt number (1-based) and returns the report.
    /// Takes precedence over <see cref="DefaultReport"/>.
    /// </summary>
    public Func<int, QualityGateReport>? ReportFactory { get; set; }

    /// <summary>Default report returned when ReportFactory is not set.</summary>
    public QualityGateReport DefaultReport { get; set; } = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public int AttemptCount => _attemptCount;

    public void Reset()
    {
        _attemptCount = 0;
        ReportFactory = null;
        DefaultReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };
    }

    /// <summary>Configures the validator to always pass.</summary>
    public void AlwaysPass()
    {
        ReportFactory = null;
        DefaultReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };
    }

    /// <summary>Configures the validator to always fail.</summary>
    public void AlwaysFail(string reason = "Tests failed")
    {
        ReportFactory = null;
        DefaultReport = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = false, Details = reason }
        };
    }

    /// <summary>Configures the validator to fail N times then pass.</summary>
    public void FailThenPass(int failCount)
    {
        ReportFactory = attempt => attempt <= failCount
            ? new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = false, Details = $"Failed on attempt {attempt}" }
            }
            : new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
            };
    }

    public Task<QualityGateReport> ValidateAsync(string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct, string? baseBranch = null)
    {
        var attempt = Interlocked.Increment(ref _attemptCount);
        var report = ReportFactory?.Invoke(attempt) ?? DefaultReport;
        return Task.FromResult(report);
    }
}
