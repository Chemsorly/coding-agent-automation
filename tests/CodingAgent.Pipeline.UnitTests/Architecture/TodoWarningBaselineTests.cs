namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Source-scan guard: enforces a non-increasing baseline on 'TODO [WARNING]' comments in src/.
///
/// <para>
/// Background: grep across src/**/*.cs found 309 'TODO [WARNING]' comments — review findings
/// parked as comments that no quality gate tracks. The project convention is to use 'NOTE' (not
/// 'TODO') in production code to satisfy SonarQube S1135. Full remediation of all files at once
/// exceeds the scope limit, so this guard pins the current count as a non-increasing baseline.
/// </para>
///
/// <para>
/// HOW THE GUARD WORKS:
/// The test fails when the actual count in src/**/*.cs EXCEEDS <see cref="BaselineCount"/>.
/// Introductions of new 'TODO [WARNING]' comments fail the build immediately.
/// Reductions lower the actual count below the baseline, causing the second assertion to fail —
/// requiring the developer to re-pin <see cref="BaselineCount"/> to the new (lower) value so the
/// guard cannot silently pass against a stale higher baseline.
/// </para>
///
/// <para>
/// WHEN TO UPDATE THIS FILE:
/// After converting a batch of 'TODO [WARNING]' comments to 'NOTE', run the tests. The second
/// assertion will fail and report the new (lower) count. Set <see cref="BaselineCount"/> to that
/// value and commit. The guard then enforces the new lower baseline going forward.
/// </para>
///
/// <para>
/// CONVENTION: Converted comments use 'NOTE' (not 'TODO') per the S1135 convention, and should
/// include an issue reference, e.g.: // NOTE (issue #NNNN): ...
/// </para>
/// </summary>
public class TodoWarningBaselineTests
{
    // ── Baseline ─────────────────────────────────────────────────────────────
    // Re-pin this constant downward whenever a legitimate reduction lands.
    // The guard will fail (second assertion) until this value matches the new lower count.
    // TODO [WARNING]: The pre-change baseline figure is inconsistent between sources: the issue
    // description states 309, but the arithmetic (288 + 13 WorkItemDispatchEndpoints conversions
    // + 9 QualityGateExecutor.RetryLoop conversions = 310) is consistent with 310. The value 288
    // is independently verifiable (grep src/**/*.cs) and is correct. Reconcile the issue description
    // or this comment to agree on the pre-change count to avoid misleading future maintainers.
    // Pinned at: 287 (reduced from pre-change baseline of ~310 by converting QualityGateExecutor.RetryLoop.cs,
    // WorkItemDispatchEndpoints.cs, and CreateBranchStep.cs — issue #3243).
    private const int BaselineCount = 287;

    // ── Repo-root resolution (identical to SonarGateBugConditionTests) ────────
    // TODO (DotNetSpecialist [WARNING]): GetRepoRoot() is called during static property initialization.
    // If GetRepoRoot throws (e.g. *.sln not found in any ancestor — possible in some CI sandbox layouts
    // where the test binary is extracted to a temp path), the TypeInitializationException is thrown at
    // test collection time, producing a misleading xUnit runner error rather than a clear test failure
    // message. This is an inherited pattern from SonarGateBugConditionTests; consider wrapping the call
    // in a try/catch inside the test method instead of using a static initializer.
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

    // ── Guard test ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all *.cs files under src/ and asserts that the number of 'TODO [WARNING]' comment
    /// lines equals the pinned <see cref="BaselineCount"/>. Fails in two directions:
    /// <list type="bullet">
    ///   <item>Actual &gt; Baseline — a new 'TODO [WARNING]' was introduced; convert it to 'NOTE'.</item>
    ///   <item>Actual &lt; Baseline — the count was reduced; re-pin the baseline to the new value.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void TodoWarningCount_InSrc_MustNotExceedBaseline_AndBaselineMustBeAccurate()
    {
        var srcPath = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(srcPath),
            $"src/ directory not found at expected path: {srcPath}");

        // Collect all matching lines across src/**/*.cs
        // TODO (DotNetSpecialist [WARNING]): Directory.EnumerateFiles with SearchOption.AllDirectories
        // has no guard against symlink cycles. On a repo with symlinked directories this could loop
        // indefinitely. This is low-impact for normal CI runs; if symlinks are ever introduced under
        // src/, add EnumerationOptions with MaxRecursionDepth or detect and skip symlink entries.
        var matchingFiles = new List<(string RelativePath, int LineNumber, string Line)>();
        foreach (var file in Directory.EnumerateFiles(srcPath, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Match both "TODO [WARNING]" and "TODO: [WARNING]" patterns used in the codebase.
                if (line.Contains("TODO [WARNING]", StringComparison.Ordinal) ||
                    line.Contains("TODO: [WARNING]", StringComparison.Ordinal))
                {
                    var relativePath = Path.GetRelativePath(RepoRoot, file);
                    matchingFiles.Add((relativePath, i + 1, line.Trim()));
                }
            }
        }

        var actualCount = matchingFiles.Count;

        // Assertion 1: no new 'TODO [WARNING]' was introduced above the pinned baseline.
        Assert.True(
            actualCount <= BaselineCount,
            $"TODO [WARNING] guard: count in src/ ({actualCount}) exceeds the pinned baseline ({BaselineCount}).\n" +
            $"Convert the new occurrence(s) to 'NOTE (issue #NNNN): ...' per the S1135 convention.\n\n" +
            $"All {actualCount} occurrences:\n" +
            string.Join("\n", matchingFiles.Select(m => $"  {m.RelativePath}:{m.LineNumber}: {m.Line}")));

        // Assertion 2: the baseline is still accurate (was re-pinned after a reduction).
        // This prevents the guard from silently passing against a stale higher baseline value
        // after a batch of 'TODO [WARNING]' comments has been converted to 'NOTE'.
        Assert.True(
            actualCount >= BaselineCount,
            $"TODO [WARNING] guard: count in src/ ({actualCount}) is LOWER than the pinned baseline ({BaselineCount}).\n" +
            $"The baseline must be re-pinned to the new lower count.\n" +
            $"Update BaselineCount in {nameof(TodoWarningBaselineTests)} to {actualCount}.");
    }
}
