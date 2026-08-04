using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Bug condition exploration tests for SonarQube quality gate failures.
/// Validates: Requirements 1.52, 1.53, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6
///
/// CRITICAL: These tests MUST FAIL on unfixed code.
/// Failure confirms the gate-blocking bugs exist.
/// Tests encode the expected (correct) behavior — they will pass after the fix is applied.
///
/// Property 1: Bug Condition — SonarQube Quality Gate Failures
/// Two concrete gate-blocking cases:
///   1. sonar.yml has no `dotnet test --collect` step → new_coverage = 0%
///   2. Test methods have zero assertions → new_reliability_rating = C
/// </summary>
public class SonarGateBugConditionTests
{
    // Resolve repo root from the test assembly location (bin/Debug/net10.0 → up 5 levels)
    private static string RepoRoot { get; } = GetRepoRoot();

    private static string GetRepoRoot()
    {
        // Walk up from the executing assembly until we find the .sln file
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !dir.GetFiles("*.sln").Any())
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not locate repo root (*.sln) starting from {AppContext.BaseDirectory}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — sonar.yml structural assertion
    // EXPECTED TO FAIL: sonar.yml has no dotnet test --collect step
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 1.52, 1.53
    /// Property: sonar.yml contains a `dotnet test` step with
    ///           `--collect:"XPlat Code Coverage"` between sonarscanner begin and end.
    /// EXPECTED OUTCOME ON UNFIXED CODE: FAIL
    /// Counterexample: "sonar.yml has no dotnet test step between scanner begin and end"
    /// </summary>
    [Fact]
    public void SonarYml_MustHaveDotnetTestCollectStepBetweenScannerBeginAndEnd()
    {
        var sonarYmlPath = Path.Combine(RepoRoot, ".github", "workflows", "sonar.yml");
        Assert.True(File.Exists(sonarYmlPath),
            $"sonar.yml not found at expected path: {sonarYmlPath}");

        var content = File.ReadAllText(sonarYmlPath);

        // Locate sonarscanner begin and end boundaries
        var beginIndex = content.IndexOf("sonarscanner begin", StringComparison.OrdinalIgnoreCase);
        var endIndex = content.IndexOf("sonarscanner end", StringComparison.OrdinalIgnoreCase);

        Assert.True(beginIndex >= 0,
            "sonar.yml does not contain 'sonarscanner begin' command. Sonar workflow structure is broken.");
        Assert.True(endIndex >= 0,
            "sonar.yml does not contain 'sonarscanner end' command. Sonar workflow structure is broken.");
        Assert.True(endIndex > beginIndex,
            "sonarscanner end appears before sonarscanner begin — workflow is malformed.");

        // Extract the content between begin and end
        var between = content.Substring(beginIndex, endIndex - beginIndex);

        // Assert dotnet test with --collect:"XPlat Code Coverage" is present in that section
        // This is what produces the coverage.opencover.xml that SonarQube needs
        var hasDotnetTest = between.Contains("dotnet test", StringComparison.OrdinalIgnoreCase);
        var hasCollectFlag = between.Contains("--collect", StringComparison.OrdinalIgnoreCase) ||
                             between.Contains("XPlat Code Coverage", StringComparison.OrdinalIgnoreCase);

        Assert.True(hasDotnetTest && hasCollectFlag,
            $"BUG CONFIRMED: sonar.yml has no 'dotnet test --collect:\"XPlat Code Coverage\"' step " +
            $"between sonarscanner begin and end. " +
            $"new_coverage will be reported as 0% by SonarQube (gate requires ≥80%). " +
            $"Content between begin/end:\n{between.Trim()}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — assertion-free test methods
    // EXPECTED TO FAIL: multiple test methods have no Assert.* calls
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 1.1–1.6
    /// Property: each listed test method body contains at least one Assert.* call.
    /// EXPECTED OUTCOME ON UNFIXED CODE: FAIL
    /// Counterexample: "UndoSnackbarComponentTests line 61 has no assertion"
    /// </summary>
    [Fact]
    public void AllListedTestMethods_MustContainAtLeastOneAssertion()
    {
        // Each entry: (relative file path from repo root, 1-based line number of [Fact]/[Theory], description)
        var testLocations = new (string RelativePath, int LineNumber, string Description)[]
        {
            // Original 6 from bugfix.md §1.1-1.6
            ("tests/CodingAgentWebUI.UnitTests/Components/UndoSnackbarComponentTests.cs",      61,  "UndoSnackbarComponentTests line 61 (UndoSnackbar_Dispose_CancelsPendingDismiss)"),
            ("tests/CodingAgentWebUI.UnitTests/Components/UndoSnackbarComponentTests.cs",      97,  "UndoSnackbarComponentTests line 97 (UndoSnackbar_Dispose_WhenNeverShown)"),
            ("tests/CodingAgentWebUI.E2ETests/Tests/K8sChatIntegrationTests.cs",              124,  "K8sChatIntegrationTests line 124"),
            ("tests/CodingAgentWebUI.UnitTests/Dispatch/ChatJobDispatcherTests.cs",           637,  "ChatJobDispatcherTests line 637"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Telemetry/SerilogOtlpExtensionsTests.cs", 170, "SerilogOtlpExtensionsTests line 170"),
            ("tests/CodingAgentWebUI.Agent.UnitTests/PipelineCleanupTests.cs",                196,  "PipelineCleanupTests line 196"),

            // Additional 14
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/Services/PostgresLeaderElectionServiceTests.cs",         213, "PostgresLeaderElectionServiceTests line 213"),
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/Services/AgentHubFacadeProgressTrackingTests.cs",        122, "AgentHubFacadeProgressTrackingTests line 122"),
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/Services/AgentHubFacadeProgressTrackingTests.cs",        129, "AgentHubFacadeProgressTrackingTests line 129"),
            ("tests/CodingAgentWebUI.UnitTests/Hubs/AgentHubFacadeTransitionTests.cs",                           93,  "AgentHubFacadeTransitionTests line 93"),
            ("tests/CodingAgentWebUI.UnitTests/Hubs/AgentHubFacadeTransitionTests.cs",                          100,  "AgentHubFacadeTransitionTests line 100"),
            ("tests/CodingAgentWebUI.Agent.UnitTests/CriticalMessageBufferTests.cs",                            191,  "CriticalMessageBufferTests line 191"),
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/ProviderDisposerTests.cs",                                6,  "ProviderDisposerTests line 6 (DisposeAllAsync_NullProvider_Skips)"),
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/ProviderDisposerTests.cs",                               13,  "ProviderDisposerTests line 13 (DisposeAllAsync_NonDisposable_Skips)"),
            ("tests/CodingAgentWebUI.Pipeline.UnitTests/ProviderDisposerTests.cs",                               50,  "ProviderDisposerTests line 50 (DisposeAllAsync_EmptyArray_NoOp)"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Persistence/WorkItemStateMachinePropertyTests.cs", 136, "WorkItemStateMachinePropertyTests line 136"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Persistence/WorkItemStateMachinePropertyTests.cs", 154, "WorkItemStateMachinePropertyTests line 154"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Persistence/WorkItemStateMachinePropertyTests.cs", 169, "WorkItemStateMachinePropertyTests line 169"),
            ("tests/CodingAgentWebUI.IntegrationTests/Smoke/DbModeSmokeTests.cs",                                36,  "DbModeSmokeTests line 36"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Locking/InProcessDistributedLockProviderTests.cs", 44,  "InProcessDistributedLockProviderTests line 44"),
            ("tests/CodingAgentWebUI.Infrastructure.UnitTests/Locking/InProcessDistributedLockProviderTests.cs", 93,  "InProcessDistributedLockProviderTests line 93"),
            ("tests/CodingAgentWebUI.UnitTests/DispatchOrchestrationServiceTests.cs",                          1232,  "DispatchOrchestrationServiceTests line 1233 (RevertFailedDistribution_SwapsLabelBackToNext)"),
            ("tests/CodingAgentWebUI.Agent.UnitTests/OpenCode/OpenCodeHealthMonitorTests.cs",                   165,  "OpenCodeHealthMonitorTests line 165"),
        };

        // Pattern: any Assert.*, FluentAssertions .Should(), Moq .Verify(), or AwesomeAssertions Should*
        // SonarQube S2699 recognizes all of these as valid assertions.
        var assertionPattern = new Regex(
            @"\bAssert\.|\.Should\b|\.Verify\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var failures = new List<string>();

        foreach (var (relativePath, lineNumber, description) in testLocations)
        {
            var fullPath = Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                // File doesn't exist — treat as a failure (can't have assertions if file is missing)
                failures.Add($"  FILE NOT FOUND: {description}\n    Path: {fullPath}");
                continue;
            }

            var lines = File.ReadAllLines(fullPath);
            var methodBody = ExtractMethodBodyStartingAtLine(lines, lineNumber);

            if (methodBody is null)
            {
                failures.Add($"  COULD NOT PARSE METHOD BODY: {description}\n    (line {lineNumber} in {relativePath})");
                continue;
            }

            var hasAssertion = assertionPattern.IsMatch(methodBody);
            if (!hasAssertion)
            {
                failures.Add($"  NO ASSERTION: {description}\n    Method body:\n{IndentBody(methodBody)}");
            }
        }

        Assert.True(failures.Count == 0,
            $"BUG CONFIRMED: {failures.Count} test method(s) have no Assert.* call.\n" +
            $"These drive new_reliability_rating = C (gate requires A).\n\n" +
            $"Failing cases:\n{string.Join("\n", failures)}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the method body (from the opening brace to the matching closing brace)
    /// for the test method whose [Fact]/[Theory]/[Property] attribute or method declaration
    /// starts at or near the given 1-based line number.
    /// Returns null if the method boundary cannot be determined.
    /// </summary>
    private static string? ExtractMethodBodyStartingAtLine(string[] lines, int startLineNumber)
    {
        if (startLineNumber < 1 || startLineNumber > lines.Length)
            return null;

        // Search forward from the given line for the opening brace of the method body
        // The line number points to the [Fact] attribute or method signature
        var searchStart = startLineNumber - 1; // convert to 0-based index

        // Find the opening '{' of the method (skip attribute lines, modifiers, etc.)
        int openBraceLineIndex = -1;
        for (int i = searchStart; i < Math.Min(lines.Length, searchStart + 20); i++)
        {
            if (lines[i].Contains('{'))
            {
                openBraceLineIndex = i;
                break;
            }
        }

        if (openBraceLineIndex < 0)
            return null;

        // Walk forward balancing braces to find the end of the method
        int depth = 0;
        int closeBraceLineIndex = -1;
        for (int i = openBraceLineIndex; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBraceLineIndex = i;
                        break;
                    }
                }
            }
            if (closeBraceLineIndex >= 0) break;
        }

        if (closeBraceLineIndex < 0)
            return null;

        return string.Join('\n', lines, openBraceLineIndex, closeBraceLineIndex - openBraceLineIndex + 1);
    }

    private static string IndentBody(string body)
    {
        var bodyLines = body.Split('\n');
        return string.Join('\n', bodyLines.Select(l => "    " + l));
    }
}
