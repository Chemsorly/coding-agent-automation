using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Controls whether review agents share the codegen session or run in isolation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewIsolation
{
    /// <summary>Review agents share the codegen session (legacy behavior).</summary>
    Shared,

    /// <summary>Review agents run in fresh sessions with no shared context.</summary>
    Isolated
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

    /// <summary>
    /// Controls whether review agents share the codegen session or run in fresh isolated sessions.
    /// Default is Isolated to eliminate self-attribution bias.
    /// </summary>
    [Key(3)]
    public ReviewIsolation ReviewIsolation { get; init; } = ReviewIsolation.Isolated;
}
