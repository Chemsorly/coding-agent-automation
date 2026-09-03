using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Controls whether review agents run in isolation.
/// Isolated is the zero value so that default(ReviewIsolation) matches the intended default
/// — MessagePack source-generated formatters use default(T) for missing array elements.
///
/// ReviewIsolation.Shared was removed in #2233 (decision #1042). The custom
/// <see cref="ReviewIsolationJsonConverter"/> maps any unknown string value (including the
/// legacy "Shared") to <see cref="Isolated"/> so that old stored JSON configs deserialize
/// without error. The [JsonConverter] attribute is on the enum type itself so that the
/// fallback applies regardless of which JsonSerializerOptions instance is in use.
/// </summary>
[JsonConverter(typeof(ReviewIsolationJsonConverter))]
public enum ReviewIsolation
{
    /// <summary>Review agents run in fresh sessions with no shared context (default).</summary>
    Isolated = 0,
}

/// <summary>
/// Custom JSON converter for <see cref="ReviewIsolation"/> that provides graceful migration
/// for stored configs that still contain the legacy "Shared" string value. Any unknown value
/// (including "Shared") maps to <see cref="ReviewIsolation.Isolated"/>.
///
/// Registered at the type level so that it takes precedence over any global
/// <see cref="JsonStringEnumConverter"/> in <see cref="PipelineJsonOptions"/>.
/// </summary>
public sealed class ReviewIsolationJsonConverter : JsonConverter<ReviewIsolation>
{
    public override ReviewIsolation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // TODO: reader.TokenType is not checked before calling GetString(). If the JSON value is
        // a number (e.g. "reviewIsolation": 0 or 1 — valid under the old JsonStringEnumConverter
        // with allowIntegerValues:true, or produced by round-tripping MessagePack-integer payloads
        // through JSON), GetString() throws InvalidOperationException. Add a
        // reader.TokenType == JsonTokenType.Number guard that reads the integer and maps it to
        // ReviewIsolation.Isolated to maintain the graceful-migration contract for integer inputs.
        var value = reader.GetString();
        if (string.Equals(value, nameof(ReviewIsolation.Isolated), StringComparison.OrdinalIgnoreCase))
            return ReviewIsolation.Isolated;

        // Unknown value (e.g. legacy "Shared") — map to Isolated for graceful migration.
        return ReviewIsolation.Isolated;
    }

    public override void Write(Utf8JsonWriter writer, ReviewIsolation value, JsonSerializerOptions options)
    {
        // Always write as a quoted string to match JsonStringEnumConverter behaviour.
        writer.WriteStringValue(nameof(ReviewIsolation.Isolated));
    }
}

[MessagePackObject]
public sealed record CodeReviewConfiguration
{
    /// <summary>
    /// When set, the review step splits into find-then-fix: the review prompt reports findings
    /// with severity markers, then this fix prompt is sent only if [CRITICAL] findings exist.
    /// When null/empty, falls back to single-pass behavior (review prompt does both find and fix).
    /// </summary>
    [Key(0)]
    public string? FixPrompt { get; init; }

    /// <summary>
    /// Settings controlling inline review comment behavior: severity threshold,
    /// maximum comments, verbosity ordering, retry count, and enablement.
    /// Defaults to a new instance with Enabled=true, ensuring inline comments
    /// are active by default when the key is absent from configuration files.
    /// </summary>
    [Key(1)]
    public InlineCommentSettings InlineComments { get; init; } = new();

    [Key(2)]
    public int MaxIterations { get; init; } = 2;

    // Key(3) is retired — was previously used, then briefly reused for ReviewIsolation.
    // Old payloads carry a stale value at index 3 (typically 0). Moved to Key(4) so that
    // stale Key(3) values are silently ignored and ReviewIsolation defaults to Isolated.

    /// <summary>
    /// Controls whether review agents run in fresh isolated sessions.
    /// Always Isolated — the only valid value since ReviewIsolation.Shared was removed in #2233.
    /// Retained at Key(4) for wire compatibility; stored MessagePack payloads with integer 1
    /// at Key(4) deserialize to (ReviewIsolation)1 (unnamed) which is safe because execution
    /// unconditionally uses UseResume = false regardless of this field's value.
    /// </summary>
    [Key(4)]
    public ReviewIsolation ReviewIsolation { get; init; } = ReviewIsolation.Isolated;

    /// <summary>
    /// Deep-merges the given overrides into this configuration. Only non-null properties
    /// in the overrides record replace the corresponding values; null properties are left unchanged.
    /// </summary>
    public CodeReviewConfiguration ApplyOverrides(CodeReviewOverrides overrides)
    {
        var result = this;
        // TODO: Nullable sentinel pattern means a project cannot override FixPrompt to null
        // (which disables fix prompts). null here means "don't override," so there is no way
        // to express "clear this value." Consider a sentinel pattern if this becomes a requirement.
        if (overrides.FixPrompt is not null)
            result = result with { FixPrompt = overrides.FixPrompt };
        if (overrides.MaxIterations.HasValue)
            result = result with { MaxIterations = overrides.MaxIterations.Value };
        if (overrides.ReviewIsolation.HasValue)
            result = result with { ReviewIsolation = overrides.ReviewIsolation.Value };
        if (overrides.InlineComments is not null)
            result = result with { InlineComments = result.InlineComments.ApplyOverrides(overrides.InlineComments) };
        return result;
    }
}

/// <summary>
/// Nullable override record for <see cref="CodeReviewConfiguration"/>.
/// Used on <see cref="PipelineProject"/> to express partial overrides:
/// null properties mean "inherit from global config" rather than "set to default."
/// </summary>
public sealed record CodeReviewOverrides
{
    public string? FixPrompt { get; init; }
    public InlineCommentOverrides? InlineComments { get; init; }
    public int? MaxIterations { get; init; }
    public ReviewIsolation? ReviewIsolation { get; init; }
}
