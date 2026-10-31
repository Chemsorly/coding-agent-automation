using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Models;

/// <summary>The conventional commit types the pipeline writes into PR titles and commit messages.</summary>
public static partial class ConventionalCommitTypes
{
    public const string Feature = "feat";
    public const string Fix = "fix";
    public const string Refactor = "refactor";
    public const string Test = "test";
    public const string Docs = "docs";
    public const string Chore = "chore";

    private const string BugLabel = "bug";

    private static readonly Dictionary<string, string> s_typeByScanCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        [RefactoringCategories.Duplication] = Refactor,
        [RefactoringCategories.StructuralDrift] = Refactor,
        [RefactoringCategories.Complexity] = Refactor,
        [RefactoringCategories.OverEngineering] = Refactor,
        [RefactoringCategories.DeadCode] = Refactor,
        [RefactoringCategories.NamingInconsistency] = Refactor,
        [RefactoringCategories.PrimitiveObsession] = Refactor,
        [RefactoringCategories.Bug] = Fix,
        [RefactoringCategories.TestGap] = Test,
        [RefactoringCategories.StaleDocumentation] = Docs,
        [RefactoringCategories.Todo] = Chore
    };

    /// <summary>
    /// Resolves the commit type of an issue: the category line of a refactoring scan issue, then
    /// the <c>bug</c> label, then <see cref="Feature"/>. The category only counts on a scan issue,
    /// so a human issue that quotes a scan's metadata line keeps its own classification.
    /// </summary>
    public static string Resolve(string? issueDescription, IReadOnlyList<string> issueLabels)
    {
        if (RefactoringScanIssues.IsScanDescription(issueDescription)
            && ScanCategoryLine().Match(issueDescription!) is { Success: true } match
            && s_typeByScanCategory.TryGetValue(match.Groups["category"].Value, out var scanType))
        {
            return scanType;
        }

        return issueLabels.Contains(BugLabel, StringComparer.OrdinalIgnoreCase) ? Fix : Feature;
    }

    [GeneratedRegex(@"^\*\*Category:\*\*[ \t]*(?<category>[A-Za-z-]+)[ \t]*(?:\||\r?$)", RegexOptions.Multiline)]
    private static partial Regex ScanCategoryLine();
}
