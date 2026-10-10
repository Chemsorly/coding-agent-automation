using System.Text;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Detects branch changes to files that configure quality gates (analyzers, coverage, lint or CI),
/// so the pipeline can leave such a PR as a draft for a person to review instead of marking it ready.
/// The file list is fixed in code on purpose: it is a guard against agents weakening a check.
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
    /// Lists the files the branch in <paramref name="workspacePath"/> changed against <c>origin/main</c>.
    /// Returns an empty list when the workspace is missing or is not a git repository.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> GetChangedFilesAsync(string? workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workspacePath) || !Directory.Exists(Path.Combine(workspacePath, ".git")))
            return [];

        var output = await GitProcessRunner.RunAsync(workspacePath, "diff --name-only origin/main...HEAD", ct, throwOnNonZeroExit: false);
        return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
        sb.Append("\n## ⚠️ Quality-gate configuration changed\n");
        sb.Append('\n');
        sb.Append("This PR changes files that configure quality gates (analyzers, coverage, lint or CI). The pipeline left it as a draft so that a person reviews these changes before merging:\n");
        sb.Append('\n');
        foreach (var file in files)
            sb.Append("- `").Append(file).Append("`\n");
        sb.Append('\n');
        sb.Append("If the change is intended, mark the PR ready for review. If not, revert these files.\n");
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
