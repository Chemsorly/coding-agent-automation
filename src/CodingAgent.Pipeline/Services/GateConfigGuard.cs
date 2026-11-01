using System.Text;

namespace CodingAgent.Pipeline.Services;

/// <summary>What the quality-gate configuration guard found. The values are the <c>pipeline.pr.guard_result</c> tag.</summary>
internal enum GateConfigGuardStatus { Clean, Changed, Unverifiable }

/// <summary>The guard's result. <paramref name="GateFiles"/> is empty unless the status is Changed.</summary>
internal sealed record GateConfigGuardResult(GateConfigGuardStatus Status, IReadOnlyList<string> GateFiles);

/// <summary>
/// Detects branch changes to files that configure quality gates (analyzers, coverage, lint or CI),
/// so the pipeline can leave such a PR as a draft for a person to review instead of marking it ready.
/// The file list is fixed in code on purpose: it is a guard against agents weakening a check.
/// The list of changed files comes from the provider, never from the workspace, whose refs an agent can change.
/// </summary>
public static class GateConfigGuard
{
    // File names that configure a quality gate, in any directory.
    private static readonly string[] FileNames =
    [
        "sonar-project.properties", "SonarQube.Analysis.xml", ".sonarcloud.properties",
        ".editorconfig", "codecov.yml", ".codecov.yml", ".coveragerc",
        ".gitlab-ci.yml", ".eslintignore",
    ];
    // File extensions that configure analyzers or test/coverage runs.
    private static readonly string[] Extensions = [".globalconfig", ".ruleset", ".runsettings"];
    // File-name prefixes (ESLint config variants).
    private static readonly string[] FileNamePrefixes = [".eslintrc", "eslint.config."];
    // Repository-relative directory prefixes.
    private static readonly string[] PathPrefixes = [".github/workflows/"];

    internal const string WarningHeading = "## ⚠️ Quality-gate configuration changed";
    internal const string UnverifiableHeading = "## ⚠️ Could not verify the changed files";

    /// <summary>
    /// Returns the changed paths that configure a quality gate, in input order and without duplicates.
    /// Paths are repository-relative; <c>\</c> is normalised to <c>/</c> and matching ignores case.
    /// </summary>
    internal static IReadOnlyList<string> FindGateConfigFiles(IEnumerable<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in changedFiles)
        {
            var path = raw?.Trim();
            if (string.IsNullOrEmpty(path))
                continue;

            if (IsGateConfigFile(path.Replace('\\', '/')) && seen.Add(path))
                result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// Returns <paramref name="body"/> followed by a warning section that lists each changed
    /// quality-gate configuration file as a bullet.
    /// </summary>
    internal static string AppendWarningSection(string body, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(files);

        var sb = new StringBuilder(body);
        sb.Append('\n');
        sb.Append('\n').Append(WarningHeading).Append('\n');
        sb.Append('\n');
        sb.Append("This PR changes files that configure quality gates (analyzers, coverage, lint or CI). The pipeline left it as a draft so that a person reviews these changes before merging:\n");
        sb.Append('\n');
        foreach (var file in files)
            sb.Append("- `").Append(file).Append("`\n");
        sb.Append('\n');
        sb.Append("If the change is intended, mark the PR ready for review. If not, revert these files.\n");
        return sb.ToString();
    }

    /// <summary>
    /// Returns <paramref name="body"/> followed by a section saying the PR's changed files could not be
    /// read completely, so the guard could not check them.
    /// </summary>
    internal static string AppendUnverifiableSection(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var sb = new StringBuilder(body);
        sb.Append('\n');
        sb.Append('\n').Append(UnverifiableHeading).Append('\n');
        sb.Append('\n');
        sb.Append("The pipeline could not read this PR's complete list of changed files from the provider, so it could not check whether quality-gate configuration changed. It left the PR as a draft so that a person reviews it.\n");
        sb.Append('\n');
        sb.Append("Check the changed files, then mark the PR ready for review.\n");
        return sb.ToString();
    }

    private static bool IsGateConfigFile(string path)
    {
        var slash = path.LastIndexOf('/');
        var fileName = slash >= 0 ? path[(slash + 1)..] : path;

        return FileNames.Any(n => fileName.Equals(n, StringComparison.OrdinalIgnoreCase))
            || Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || FileNamePrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || PathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
