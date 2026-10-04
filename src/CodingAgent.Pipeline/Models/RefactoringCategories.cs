namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The finding categories of the refactoring scan. The detection agents emit them, the
/// aggregation keeps the category of the finding a proposal comes from, and issue creation
/// rejects a proposal whose category is not in <see cref="All"/>.
/// </summary>
public static class RefactoringCategories
{
    // Agent A: structural debt
    public const string Duplication = "duplication";
    public const string StructuralDrift = "structural-drift";
    public const string Complexity = "complexity";
    public const string OverEngineering = "over-engineering";

    // Agent B: correctness and hygiene
    public const string Todo = "todo";
    public const string DeadCode = "dead-code";
    public const string Bug = "bug";
    public const string StaleDocumentation = "stale-documentation";

    // Agent C: design consistency
    public const string NamingInconsistency = "naming-inconsistency";
    public const string PrimitiveObsession = "primitive-obsession";

    public static readonly IReadOnlyList<string> Structural = [Duplication, StructuralDrift, Complexity, OverEngineering];
    public static readonly IReadOnlyList<string> Correctness = [Todo, DeadCode, Bug, StaleDocumentation];
    public static readonly IReadOnlyList<string> Design = [NamingInconsistency, PrimitiveObsession];

    /// <summary>Every valid category, in agent order.</summary>
    public static readonly IReadOnlyList<string> All = [.. Structural, .. Correctness, .. Design];

    /// <summary>Returns <c>true</c> when <paramref name="category"/> is a known category (case-insensitive).</summary>
    public static bool IsKnown(string? category) =>
        category is not null && All.Contains(category.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Formats a category list for a JSON schema example, e.g. <c>"a|b|c"</c>.</summary>
    public static string ToSchemaList(IEnumerable<string> categories) => string.Join("|", categories);
}
