namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Source-scan guard that prevents new 'TODO [WARNING]' comments from being introduced in src/.
///
/// Background: A grep across src/**/*.cs found 174 'TODO [WARNING]' comments — review findings
/// parked as comments that no quality gate tracks. The project convention is to use 'NOTE' (not
/// 'TODO') in production code to satisfy SonarQube S1135. This test pins the count as a
/// non-increasing baseline so that any new 'TODO [WARNING]' introduction fails the build.
///
/// Baseline management: This constant must be re-pinned DOWNWARD whenever a legitimate reduction
/// lands (convert or delete TODO [WARNING] comments in src/). The constant must never be bumped
/// upward — that would defeat the purpose of the guard. To reduce the baseline, convert
/// 'TODO [WARNING]' to 'NOTE (issue #NNNN):' per the S1135 convention, then lower the constant.
///
/// Scope: src/**/*.cs only (excluding obj/ build artefacts). The tests/ tree contains ~569
/// additional occurrences in test data and assertions — those are intentional and excluded.
///
/// Counting method: occurrence-level (File.ReadAllText + rolling IndexOf), which matches
/// grep -r "TODO \[WARNING\]" src/ --include="*.cs" | wc -l.
/// </summary>
public class TodoWarningBaselineTests
{
    /// <summary>
    /// Pinned baseline: the exact count of 'TODO [WARNING]' occurrences in src/**/*.cs after
    /// the companion fixes in QualityGateExecutor.RetryLoop.cs and WorkItemDispatchEndpoints.cs
    /// landed in issue #3243.
    ///
    /// Post-change count: 163. Verified via `grep -r "TODO \[WARNING\]" src/ --include="*.cs" | wc -l`.
    ///
    // NOTE (issue #3243): The original docstring stated "Pre-change count: 174. Post-change count: 163
    // (174 - 3 - 8 = 163)" but the actual diff removed 9 occurrences from QualityGateExecutor.RetryLoop.cs
    // and 9 from WorkItemDispatchEndpoints.cs (18 total), so the true pre-change count was 181, not 174.
    // The arithmetic was wrong; the constant 163 is correct (verified by grep at HEAD).
    // (TestQualityReviewer WARNING)
    ///
    /// To re-pin: run `grep -r "TODO \[WARNING\]" src/ --include="*.cs" | wc -l` at HEAD
    /// after your reduction and set this constant to that value.
    /// </summary>
    private const int BaselineCount = 163;

    // Repo root: walk up from the test binary directory until we find CodingAgentAutomation.sln
    // (matching the pattern used by LayerBoundaryTests.cs — the canonical template for Architecture/ tests).
    private static readonly string RepoRoot = FindRepoRoot(AppContext.BaseDirectory);

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.GetFiles("CodingAgentAutomation.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not find repo root (CodingAgentAutomation.sln) starting from '{start}'");
    }

    /// <summary>
    /// Core guard: the count of 'TODO [WARNING]' occurrences in src/**/*.cs must not exceed
    /// the pinned baseline. Fails immediately if a new occurrence is introduced.
    /// </summary>
    // NOTE (issue #3243): This test is logically subsumed by TodoWarning_BaselineConstant_MustMatchActualCount
    // (which uses `==`): there is no reachable state where this `<=` test fails but the `==` test passes.
    // A future maintainer who reads only this test as "the guard" may not realise the `==` test is the
    // effective enforcer. Consider merging both assertions into a single test or removing this one in
    // favour of relying solely on the equality guard. (TestQualityReviewer / DotNetSpecialist WARNING)
    [Fact]
    public void TodoWarning_CountInSrc_MustNotExceedBaseline()
    {
        var srcPath = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(srcPath), $"src/ directory not found at '{srcPath}' — check repo root detection.");

        // NOTE (issue #3243): The occurrence-counting scan below is duplicated verbatim in
        // TodoWarning_BaselineConstant_MustMatchActualCount. Any future change to the obj/ exclusion
        // filter, the search pattern, or the file extension must be applied in both places. A divergence
        // would cause the two tests to silently measure different things while both appearing green,
        // defeating the staleness guard. Consider extracting CountTodoWarningsInSrc() as a shared helper.
        // (TestQualityReviewer WARNING)
        var actualCount = Directory
            .EnumerateFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Sum(file => CountOccurrences(File.ReadAllText(file), "TODO [WARNING]"));

        Assert.True(
            actualCount <= BaselineCount,
            $"TODO [WARNING] count in src/ must not exceed baseline {BaselineCount}. " +
            $"Current count: {actualCount}. " +
            $"A new 'TODO [WARNING]' comment was introduced in src/. " +
            $"Either remove the new occurrence, or convert it to 'NOTE (issue #NNNN):' per the S1135 convention " +
            $"and then lower {nameof(BaselineCount)} in {nameof(TodoWarningBaselineTests)}.cs to match the new count.");
    }

    /// <summary>
    /// Staleness guard: the baseline constant must equal the actual count, not just be >=.
    /// This prevents the baseline from drifting upward when occurrences are legitimately removed
    /// (a stale higher baseline would allow new introductions to go undetected up to the old count).
    ///
    /// When you reduce the TODO [WARNING] count in src/, you MUST also lower BaselineCount here.
    /// This test enforces that requirement.
    /// </summary>
    [Fact]
    public void TodoWarning_BaselineConstant_MustMatchActualCount()
    {
        var srcPath = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(srcPath), $"src/ directory not found at '{srcPath}' — check repo root detection.");

        var actualCount = Directory
            .EnumerateFiles(srcPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Sum(file => CountOccurrences(File.ReadAllText(file), "TODO [WARNING]"));

        Assert.True(
            actualCount == BaselineCount,
            $"TODO [WARNING] baseline constant ({BaselineCount}) is out of sync with the actual count ({actualCount}). " +
            $"If the actual count went DOWN (you removed occurrences): lower {nameof(BaselineCount)} in " +
            $"{nameof(TodoWarningBaselineTests)}.cs to {actualCount} so the guard cannot silently pass against a stale higher baseline. " +
            $"If the actual count went UP: remove the new occurrence(s) instead of raising the baseline.");
    }

    /// <summary>
    /// Counts the total number of non-overlapping occurrences of <paramref name="pattern"/>
    /// in <paramref name="text"/> using a rolling IndexOf scan (occurrence-level, not line-level).
    /// </summary>
    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
