using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// A single refactoring proposal produced by the agent.
/// Parsed from the .agent/refactoring-proposals.json file in the workspace.
/// </summary>
public sealed class RefactoringProposal
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> AffectedFiles { get; init; }
    public required string Description { get; init; }
    public required string Rationale { get; init; }
    public IReadOnlyList<string>? Prerequisites { get; init; }
    public string? EstimatedEffort { get; init; }
    public string? RiskLevel { get; init; }
    public string? Technique { get; init; }

    /// <summary>One of <see cref="RefactoringCategories.All"/>; issue creation rejects any other value.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// The decisive code or tool output, quoted verbatim with its file and line range.
    /// Rendered as a code block in the issue body.
    /// </summary>
    [JsonConverter(typeof(StringOrLinesJsonConverter))]
    public string? Evidence { get; init; }

    /// <summary>
    /// The search (e.g. <c>git grep -n 'pattern'</c>) that lists every instance the proposal must change,
    /// so the implementer and reviewer can check the change is complete.
    /// </summary>
    [JsonConverter(typeof(StringOrLinesJsonConverter))]
    public string? ScopeQuery { get; init; }

    /// <summary>
    /// Evidence sources that support this proposal. Prefixed by type:
    /// "tool:" (linter/compiler output), "hotspot:" (git frequency),
    /// "code-reading:" (manual inspection), "grep:" (pattern search),
    /// "usage-search:" (reference count). Multi-source = higher confidence.
    /// </summary>
    public IReadOnlyList<string>? EvidenceSources { get; init; }

    /// <summary>
    /// Proposal-specific acceptance criteria validated by the review agent.
    /// Each entry becomes a checkbox in the GitHub issue body.
    /// Must be verifiable from the PR diff — no subjective or unmeasurable claims.
    /// </summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>
    /// Reads a string, or an array joined with newlines; any other value is kept as its JSON text. Agents
    /// sometimes write several snippets or searches as an array; without this, one such field would fail
    /// the whole proposals file.
    /// </summary>
    internal sealed class StringOrLinesJsonConverter : JsonConverter<string?>
    {
        public override bool HandleNull => true;

        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return reader.GetString();
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Array
                ? string.Join("\n", root.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()))
                : root.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
