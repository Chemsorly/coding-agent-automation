using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for the <see cref="QualityGateExecutor.BuildQualityGateRetryPrompt"/> conditional
/// diagnostic-output vs full-diff branch, controlled by <c>hasQualityGateOutput</c>.
/// </summary>
// TODO [WARNING]: These tests only exercise the bool-overload of BuildQualityGateRetryPrompt.
// The production call site in RetryLoop.cs uses the priorRetryErrors overload, which contains
// its own independent hasQualityGateOutput derivation logic (report.Tests?.IsInfrastructureFailure).
// The derivation path in the priorRetryErrors overload is not covered by any test here, meaning:
//   - The null-conditional guard on report.Tests is unverified in that overload.
//   - The "infra failure → no diagnostic claim" routing via the priorRetryErrors overload is untested.
// Add tests calling the priorRetryErrors overload directly with infra-failure and non-infra-failure
// reports to cover the internal derivation path.
// See review finding: TestQualityReviewer WARNING — QualityGateExecutorRetryPromptTests.cs
public class QualityGateExecutorRetryPromptTests
{
    private static QualityGateReport BuildReport(bool infraFailure = false) => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult
        {
            GateName = "Tests",
            Passed = false,
            Details = infraFailure
                ? "Tests failed: process exited with code 137 — no test output produced (probable infrastructure failure, not a code failure)"
                : "Tests failed: 1 passed, 2 failed, 0 skipped.",
            IsInfrastructureFailure = infraFailure ? true : null
        }
    };

    /// <summary>
    /// AC: BuildQualityGateRetryPrompt with hasQualityGateOutput=false does NOT emit the
    /// "Diagnostic output has been written" claim and DOES emit a full-diff.txt reference.
    /// </summary>
    [Fact]
    public void BuildQualityGateRetryPrompt_NoQualityGateOutput_DoesNotContainDiagnosticClaim_ContainsFullDiff()
    {
        var report = BuildReport(infraFailure: true);

        var prompt = QualityGateExecutor.BuildQualityGateRetryPrompt(report, 1, 3, hasQualityGateOutput: false);

        prompt.Should().NotContain("Diagnostic output has been written");
        prompt.Should().Contain(AgentWorkspacePaths.FullDiffFilePath);
        prompt.Should().Contain("terminated abnormally");
    }

    /// <summary>
    /// AC: BuildQualityGateRetryPrompt with hasQualityGateOutput=true DOES emit the
    /// "Diagnostic output has been written" claim and does NOT emit a full-diff.txt reference.
    /// </summary>
    // TODO [WARNING]: The negative assertion `prompt.Should().NotContain(AgentWorkspacePaths.FullDiffFilePath)`
    // is only valid if FullDiffFilePath is not a substring of QualityGatesOutputDirectory or any other string
    // emitted in the hasQualityGateOutput=true branch. If those paths share a common prefix the assertion
    // could pass despite the full-diff reference being present. Consider asserting the exact sentence that
    // should not appear, e.g. prompt.Should().NotContain("terminated abnormally") or
    // prompt.Should().NotContain("Check `"), which are unique to the no-output branch.
    // See review finding: TestQualityReviewer WARNING — QualityGateExecutorRetryPromptTests.cs:73
    [Fact]
    public void BuildQualityGateRetryPrompt_HasQualityGateOutput_ContainsDiagnosticClaim_NoFullDiff()
    {
        var report = BuildReport(infraFailure: false);

        var prompt = QualityGateExecutor.BuildQualityGateRetryPrompt(report, 1, 3, hasQualityGateOutput: true);

        prompt.Should().Contain("Diagnostic output has been written");
        prompt.Should().Contain(AgentWorkspacePaths.QualityGatesOutputDirectory);
        prompt.Should().Contain("List the files there and read the relevant ones");
        prompt.Should().NotContain(AgentWorkspacePaths.FullDiffFilePath);
    }

    /// <summary>
    /// Both branches preserve the gate status header, attempt/maxRetries counts,
    /// and the reflect-before-fixing instructions.
    /// </summary>
    [Fact]
    public void BuildQualityGateRetryPrompt_BothBranches_PreserveCommonContent()
    {
        var report = BuildReport();

        var withOutput = QualityGateExecutor.BuildQualityGateRetryPrompt(report, 2, 5, hasQualityGateOutput: true);
        var withoutOutput = QualityGateExecutor.BuildQualityGateRetryPrompt(report, 2, 5, hasQualityGateOutput: false);

        foreach (var prompt in new[] { withOutput, withoutOutput })
        {
            prompt.Should().Contain("Quality gates failed (attempt 2/5):");
            prompt.Should().Contain("- Compilation: PASSED");
            prompt.Should().Contain("- Tests: FAILED");
            prompt.Should().Contain("Before fixing, reflect:");
            prompt.Should().Contain("Apply the targeted fix");
        }
    }
}
