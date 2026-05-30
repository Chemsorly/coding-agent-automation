using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Shared FsCheck generators for inline review comment property tests.
/// Eliminates duplication across InlineReviewFindingsParserPropertyTests,
/// InlineReviewFindingsSelectorPropertyTests, and InlineReviewFormatterPropertyTests.
/// </summary>
internal static class InlineReviewGenerators
{
    // Shared data arrays
    public static readonly string[] Severities = ["CRITICAL", "WARNING", "SUGGESTION"];
    public static readonly string[] Directories = ["src", "lib", "tests", "utils", "core", "api", "models", "services"];
    public static readonly string[] Extensions = [".cs", ".ts", ".py", ".java", ".go", ".rs"];
    public static readonly string[] FileNames = ["Service", "Controller", "Helper", "Utils", "Manager", "Handler"];
    public static readonly string[] AgentNames = ["SecurityReviewer", "CodeQuality", "PerformanceAnalyzer", "StyleChecker", "ArchitectureGuard"];
    public static readonly string[] Messages = [
        "Null reference possible",
        "Missing validation",
        "Unused variable",
        "Performance issue",
        "Security concern",
        "Deprecated API usage",
        "Missing error handling",
        "Thread safety issue"
    ];

    // Shared generators
    public static Gen<string> GenFilePath() =>
        from seg1 in Gen.Elements(Directories)
        from seg2 in Gen.Elements(Directories)
        from name in Gen.Elements(FileNames)
        from ext in Gen.Elements(Extensions)
        select $"{seg1}/{seg2}/{name}{ext}";

    public static Gen<int> GenLineNumber() => Gen.Choose(1, 9999);

    public static Gen<string> GenMessage() =>
        from msg1 in Gen.Elements(Messages)
        from msg2 in Gen.Elements(Messages)
        select $"{msg1} when {msg2}";

    public static Gen<FindingSeverity> GenSeverity() =>
        Gen.Elements(FindingSeverity.Suggestion, FindingSeverity.Warning, FindingSeverity.Critical);

    public static Gen<string> GenAgentName() => Gen.Elements(AgentNames);

    public static Gen<string> GenSeverityMarker() =>
        from sev in Gen.Elements(Severities)
        from caseVariant in Gen.Choose(0, 2)
        let formatted = caseVariant switch
        {
            0 => sev.ToUpperInvariant(),
            1 => sev.ToLowerInvariant(),
            _ => char.ToUpper(sev[0]) + sev[1..].ToLowerInvariant()
        }
        select $"[{formatted}]";

    public static Gen<string> GenSeparator() => Gen.Elements(" — ", " - ", ": ");

    public static Gen<StructuredFinding> GenFinding() =>
        from severity in GenSeverity()
        from filePath in GenFilePath()
        from lineNum in GenLineNumber()
        from message in GenMessage()
        from agent in GenAgentName()
        select new StructuredFinding
        {
            Severity = severity,
            FilePath = filePath,
            LineNumber = lineNum,
            Message = message,
            AgentName = agent
        };

    public static Gen<IReadOnlyList<StructuredFinding>> GenFindings(int minCount, int maxCount) =>
        from count in Gen.Choose(minCount, maxCount)
        from findings in Gen.ArrayOf(GenFinding(), count)
        select (IReadOnlyList<StructuredFinding>)findings.ToList();

    public static Gen<InlineCommentSettings> GenSettings() =>
        from threshold in GenSeverity()
        from maxComments in Gen.Choose(1, 50)
        from orderBySeverity in Gen.Elements(true, false)
        select new InlineCommentSettings
        {
            Enabled = true,
            SeverityThreshold = threshold,
            MaxInlineComments = maxComments,
            OrderBySeverity = orderBySeverity,
            MaxRetries = 1
        };
}
