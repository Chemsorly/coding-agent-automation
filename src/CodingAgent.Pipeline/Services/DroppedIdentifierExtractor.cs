using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Extracts C# type and member identifiers that a branch <em>added</em> in a unified diff.
/// Used to detect whether force-resolved rebase drops were later re-applied during code generation
/// (issue #3435).
/// </summary>
/// <remarks>
/// The extractor is intentionally conservative: it only inspects added lines (<c>+</c>) and applies
/// simple regex patterns. False negatives (missed identifiers) are acceptable; false positives
/// (reporting an identifier as missing when it was re-applied) are the problematic direction.
/// Only C# files are meaningfully supported. Non-C# files will produce no extractions.
/// </remarks>
public static class DroppedIdentifierExtractor
{
    // Timeout for regex operations — prevents catastrophic backtracking on adversarial input.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    // Matches C# type declarations on added diff lines:
    //   + [access-modifier] [modifiers] class|record|struct|interface|enum TypeName
    private static readonly Regex TypeDeclaration = new(
        @"^\+\s*(?:public|internal|private|protected)?\s*(?:partial\s+|sealed\s+|abstract\s+|static\s+|readonly\s+)*(?:class|record|struct|interface|enum)\s+(\w+)",
        RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout);

    // Matches C# method declarations on added diff lines:
    //   + [access-modifier] [modifiers] ReturnType MethodName(
    // Excludes property accessors (get/set/init) and common keywords used as return types
    // that have no following identifier.
    // TODO: The lazy quantifier (?:[\s\w<>\[\],?.]+?) captures the return type token when a modifier
    // keyword (static, async, override, etc.) is combined with a generic return type, e.g.
    // "public static IReadOnlyList<string> DoWork(" captures "IReadOnlyList" instead of "DoWork".
    // This is a false-positive risk: if the captured token (e.g. a custom generic) doesn't appear
    // elsewhere in the file, it is incorrectly reported as not re-applied. Consider splitting the
    // pattern into a modifier group and a return-type group to reliably skip to the method name.
    private static readonly Regex MethodDeclaration = new(
        @"^\+\s*(?:public|internal|protected|private)(?:[\s\w<>\[\],?.]+?)\s+(\w+)\s*[(<]",
        RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout);

    // Matches xUnit / NUnit test attribute lines on added diff lines:
    //   + [Fact], + [Theory], + [Test], + [TestCase]
    private static readonly Regex TestAttribute = new(
        @"^\+\s*\[(?:Fact|Theory|Test|TestCase|TestMethod)(?:\(|])",
        RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout);

    // Matches the method name on an added diff line following a test attribute.
    private static readonly Regex TestMethodName = new(
        @"^\+\s*(?:public|internal|protected|private)?(?:[\s\w]+?)\s+(\w+)\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline, RegexTimeout);

    /// <summary>
    /// Extracts identifiers (type and method names) added in a unified diff string.
    /// Only lines starting with <c>+</c> are inspected; <c>+++</c> header lines are skipped.
    /// Duplicate identifiers are deduplicated.
    /// </summary>
    public static IReadOnlyList<string> ExtractAddedIdentifiers(string unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff))
            return Array.Empty<string>();

        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        // Extract type declarations
        foreach (Match m in TypeDeclaration.Matches(unifiedDiff))
            identifiers.Add(m.Groups[1].Value);

        // Extract method declarations (non-test)
        foreach (Match m in MethodDeclaration.Matches(unifiedDiff))
        {
            var name = m.Groups[1].Value;
            // Skip common keywords and property accessors that regex can spuriously match
            if (IsKeyword(name)) continue;
            identifiers.Add(name);
        }

        // Extract test method names: look for test attribute line, then extract method name
        // from the following added line(s) within a 3-line window.
        ExtractTestMethodIdentifiers(unifiedDiff, identifiers);

        return identifiers.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Builds a dictionary of per-file extracted identifiers from the force-resolved context in a
    /// <see cref="MergeResult"/>. Files that yield no identifiers are excluded from the result.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ExtractFromMergeResult(MergeResult mergeResult)
    {
        ArgumentNullException.ThrowIfNull(mergeResult);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var ctx in mergeResult.ForceResolvedContext)
        {
            var ids = ExtractAddedIdentifiers(ctx.BranchChange);
            if (ids.Count > 0)
                result[ctx.Path] = ids;
        }
        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void ExtractTestMethodIdentifiers(string diff, HashSet<string> identifiers)
    {
        // Split into lines to perform window-based attribute→method lookup.
        var lines = diff.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TestAttribute.IsMatch(lines[i]))
                continue;

            // Look at the next 3 lines for the method declaration
            for (var j = i + 1; j < Math.Min(i + 4, lines.Length); j++)
            {
                var candidate = lines[j];
                // Must be an added line, not a context or removed line
                if (!candidate.StartsWith('+') || candidate.StartsWith("+++"))
                    continue;

                var m = TestMethodName.Match(candidate);
                if (m.Success)
                {
                    var name = m.Groups[1].Value;
                    if (!IsKeyword(name))
                        identifiers.Add(name);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// C# keywords and common tokens that can spuriously match method/type patterns.
    /// Using a HashSet avoids excessive branch conditions from a long 'is' pattern chain.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "get", "set", "init", "value", "void", "return", "new",
        "if", "else", "for", "foreach", "while", "do", "switch",
        "case", "break", "continue", "throw", "try", "catch", "finally",
        "using", "namespace", "class", "struct", "interface", "enum",
        "record", "sealed", "abstract", "static", "partial", "override",
        "virtual", "async", "await", "var", "const", "readonly",
        "public", "private", "protected", "internal", "string", "bool",
        "int", "long", "double", "float", "decimal", "object", "byte",
        "short", "uint", "ulong", "ushort", "sbyte", "char", "null",
        "true", "false", "base", "this", "params", "ref", "out", "in",
        "where", "select", "from", "orderby", "group", "join"
    };

    /// <summary>
    /// Returns true for C# keywords and common tokens that can spuriously match method/type patterns.
    /// </summary>
    private static bool IsKeyword(string name) => Keywords.Contains(name);
}
