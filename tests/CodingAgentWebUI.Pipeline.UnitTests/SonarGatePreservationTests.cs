using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Preservation property tests for SonarQube quality gate fixes.
/// Validates: Requirements 3.1, 3.11, 3.14, 3.16, 3.17
///
/// CRITICAL: These tests MUST PASS on unfixed code.
/// They are the safety net that will catch regressions during fix implementation.
///
/// Property 2: Preservation — Runtime Behavior and Workflow Auth Unchanged
///   1. sonar.yml retains SONAR_TOKEN in a step-level env: block
///      (preserving authentication after workflow edits)
/// </summary>
public class SonarGatePreservationTests
{
    // Resolve repo root from the test assembly location (walk up to find the .sln file)
    private static string RepoRoot { get; } = GetRepoRoot();

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.GetFiles("*.sln").Length == 0)
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not locate repo root (*.sln) starting from {AppContext.BaseDirectory}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — sonar.yml SONAR_TOKEN in env: block
    // EXPECTED TO PASS on unfixed code (baseline)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 3.11, 3.16
    /// Property: sonar.yml retains `SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}` in
    ///           a step-level `env:` block after any workflow changes.
    ///
    /// This preservation test ensures that:
    ///   1. The SonarQube analysis workflow continues to authenticate correctly.
    ///   2. Any edits to sonar.yml do NOT remove the env: SONAR_TOKEN variable.
    ///   3. The SONAR_TOKEN secret is NOT only passed inline in run: blocks
    ///      (satisfying S7636 — secrets must not be expanded inline).
    ///
    /// EXPECTED OUTCOME ON UNFIXED CODE: PASS (baseline green)
    /// EXPECTED OUTCOME ON FIXED CODE:   PASS (regression guard)
    ///
    /// If this test FAILS after a fix, the fix removed SONAR_TOKEN from the env:
    /// block, which will break SonarQube authentication in CI.
    /// </summary>
    [Fact]
    public void SonarYml_MustRetainSonarTokenEnvVar()
    {
        var sonarYmlPath = Path.Combine(RepoRoot, ".github", "workflows", "sonar.yml");
        Assert.True(File.Exists(sonarYmlPath),
            $"sonar.yml not found at expected path: {sonarYmlPath}");

        var content = File.ReadAllText(sonarYmlPath);

        // Assert the file contains SONAR_TOKEN: in an env: context.
        // The exact form is:
        //     env:
        //       SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
        //
        // We verify the key structural elements independently so the assertion
        // message pinpoints exactly what is missing.

        // 1. env: block must exist
        Assert.True(content.Contains("env:", StringComparison.Ordinal),
            "sonar.yml does not contain any 'env:' block. " +
            "SONAR_TOKEN authentication will be broken after workflow edits.");

        // 2. SONAR_TOKEN: key must appear
        Assert.True(content.Contains("SONAR_TOKEN:", StringComparison.Ordinal),
            "sonar.yml 'env:' block no longer contains 'SONAR_TOKEN:' key. " +
            "SonarQube authentication via environment variable is broken. " +
            "Requirement 3.16: SONAR_TOKEN must remain in the env: block.");

        // 3. The secrets reference must be present
        Assert.True(content.Contains("secrets.SONAR_TOKEN", StringComparison.Ordinal),
            "sonar.yml does not reference 'secrets.SONAR_TOKEN'. " +
            "The token source has been removed or renamed. " +
            "SonarQube CI authentication will fail.");

        // 4. SONAR_TOKEN: and env: must co-occur in the same step block.
        //    We verify this by finding an env: line that is followed by
        //    SONAR_TOKEN: within 5 lines (standard indentation).
        var lines = content.Split('\n');
        var envBlockFound = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("env:", StringComparison.Ordinal))
            {
                // Look ahead up to 5 lines for SONAR_TOKEN:
                for (int j = i + 1; j < Math.Min(lines.Length, i + 6); j++)
                {
                    if (lines[j].Contains("SONAR_TOKEN:", StringComparison.Ordinal))
                    {
                        envBlockFound = true;
                        break;
                    }
                }
            }
            if (envBlockFound) break;
        }

        Assert.True(envBlockFound,
            "sonar.yml 'env:' block does not contain 'SONAR_TOKEN:' within 5 lines. " +
            "The SONAR_TOKEN is not wired up as a step-level environment variable. " +
            "This breaks SonarQube authentication (Requirement 3.16) and will expose " +
            "the secret inline in run: blocks (violates S7636).\n\n" +
            "Expected structure:\n" +
            "    - name: Build and analyze\n" +
            "      env:\n" +
            "        SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}\n" +
            "      shell: powershell\n" +
            "      run: |\n" +
            "        ... $env:SONAR_TOKEN ...");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — sonar.yml scanner begin/end structure preserved
    // EXPECTED TO PASS on unfixed code (baseline)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 3.16
    /// Property: sonar.yml retains both sonarscanner begin and end commands in the
    ///           correct order after any workflow changes.
    ///
    /// Guards against workflow edits that accidentally remove the scanner invocation,
    /// which would silently skip SonarQube analysis entirely.
    ///
    /// EXPECTED OUTCOME ON UNFIXED CODE: PASS (baseline green)
    /// EXPECTED OUTCOME ON FIXED CODE:   PASS (regression guard)
    /// </summary>
    [Fact]
    public void SonarYml_MustRetainScannerBeginAndEnd()
    {
        var sonarYmlPath = Path.Combine(RepoRoot, ".github", "workflows", "sonar.yml");
        Assert.True(File.Exists(sonarYmlPath),
            $"sonar.yml not found at expected path: {sonarYmlPath}");

        var content = File.ReadAllText(sonarYmlPath);

        var beginIndex = content.IndexOf("sonarscanner begin", StringComparison.OrdinalIgnoreCase);
        var endIndex   = content.IndexOf("sonarscanner end",   StringComparison.OrdinalIgnoreCase);

        Assert.True(beginIndex >= 0,
            "REGRESSION: sonar.yml no longer contains 'sonarscanner begin'. " +
            "SonarQube analysis will not run in CI (Requirement 3.16).");

        Assert.True(endIndex >= 0,
            "REGRESSION: sonar.yml no longer contains 'sonarscanner end'. " +
            "SonarQube analysis results will not be published (Requirement 3.16).");

        Assert.True(endIndex > beginIndex,
            "REGRESSION: sonar.yml has sonarscanner end before sonarscanner begin. " +
            "The scanner invocation order is malformed (Requirement 3.16).");
    }
}
