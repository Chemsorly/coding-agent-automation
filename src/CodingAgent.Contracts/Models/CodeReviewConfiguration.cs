using CodingAgent.Pipeline.CodeReview.Models;
using MessagePack;
using RangeAttribute = System.ComponentModel.DataAnnotations.RangeAttribute;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Code review settings. <see cref="MaxIterations"/> and <see cref="FixPrompt"/> apply to the review step of
/// implementation runs; <see cref="InlineComments"/> applies to PR review runs, which always review once and never fix.
/// </summary>
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

    /// <summary>
    /// Review/fix cycles in implementation runs. 0 turns off the review step of implementation runs;
    /// PR review runs are unaffected.
    /// </summary>
    [Key(2)]
    [Range(0, 5)]
    public int MaxIterations { get; init; } = 2;

    // Key(3) is retired — was previously used, then briefly reused for ReviewIsolation.
    // Key(4) is retired — was ReviewIsolation, whose only value was Isolated (Shared was removed in #2233). Do NOT reuse either index.

    /// <summary>
    /// Deep-merges the given overrides into this configuration. Only non-null properties
    /// in the overrides record replace the corresponding values; null properties are left unchanged.
    /// An empty <see cref="CodeReviewOverrides.FixPrompt"/> turns the find-then-fix split off.
    /// </summary>
    public CodeReviewConfiguration ApplyOverrides(CodeReviewOverrides overrides)
    {
        var result = this;
        if (overrides.FixPrompt is not null)
            result = result with { FixPrompt = overrides.FixPrompt };
        if (overrides.MaxIterations.HasValue)
            result = result with { MaxIterations = overrides.MaxIterations.Value };
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
}
