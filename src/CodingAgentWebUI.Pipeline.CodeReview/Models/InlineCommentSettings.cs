using MessagePack;

namespace CodingAgentWebUI.Pipeline.CodeReview.Models;

/// <summary>
/// Controls inline review comment behavior: severity threshold, maximum comments,
/// verbosity ordering, retry count, and master enablement switch.
/// All properties use init-only setters for immutability.
/// Validation of ranges (MaxInlineComments 1–50, MaxRetries 0–5) is performed at usage time
/// via Math.Clamp, not at deserialization time.
/// </summary>
[MessagePackObject]
public sealed record InlineCommentSettings
{
    /// <summary>
    /// Master switch to enable or disable inline comment posting.
    /// When false, the pipeline posts body-only reviews (existing behavior)
    /// and skips FindingsParser invocation and prompt enhancement.
    /// Defaults to true — inline comments are enabled by default.
    /// Operators can disable via configuration if needed.
    /// </summary>
    [Key(0)]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Maximum number of inline comments per review submission (valid range: 1–50).
    /// When findings exceed this limit, the highest-severity findings are posted as
    /// inline comments and the remainder appear only in the body summary.
    /// Clamped at usage time via Math.Clamp.
    /// </summary>
    [Key(1)]
    public int MaxInlineComments { get; init; } = 15;

    /// <summary>
    /// Maximum number of retry attempts when the review agent does not produce
    /// structured output with file:line references. A value of 0 means no retries
    /// (only the initial attempt). Valid range: 0–5, clamped at usage time.
    /// NOTE: Each retry invokes an additional LLM API call per agent.
    /// </summary>
    [Key(2)]
    public int MaxRetries { get; init; } = 1;

    /// <summary>
    /// Controls whether inline comments are sorted by severity (Critical first,
    /// then Warning, then Suggestion) when selecting which findings to post
    /// within the MaxInlineComments limit.
    /// </summary>
    [Key(3)]
    public bool OrderBySeverity { get; init; } = true;

    /// <summary>
    /// Minimum severity level for findings to be posted as inline comments.
    /// Findings with severity below this threshold appear only in the body summary.
    /// A finding is eligible when its FindingSeverity value is >= this threshold value.
    /// </summary>
    [Key(4)]
    public FindingSeverity SeverityThreshold { get; init; } = FindingSeverity.Warning;

    /// <summary>
    /// Deep-merges the given overrides into this settings instance. Only non-null properties
    /// in the overrides record replace the corresponding values; null properties are left unchanged.
    /// </summary>
    public InlineCommentSettings ApplyOverrides(InlineCommentOverrides overrides)
    {
        var result = this;
        if (overrides.Enabled.HasValue)
            result = result with { Enabled = overrides.Enabled.Value };
        if (overrides.MaxInlineComments.HasValue)
            result = result with { MaxInlineComments = overrides.MaxInlineComments.Value };
        if (overrides.MaxRetries.HasValue)
            result = result with { MaxRetries = overrides.MaxRetries.Value };
        if (overrides.OrderBySeverity.HasValue)
            result = result with { OrderBySeverity = overrides.OrderBySeverity.Value };
        if (overrides.SeverityThreshold.HasValue)
            result = result with { SeverityThreshold = overrides.SeverityThreshold.Value };
        return result;
    }
}

/// <summary>
/// Nullable override record for <see cref="InlineCommentSettings"/>.
/// null properties mean "inherit from global config."
/// </summary>
public sealed record InlineCommentOverrides
{
    public bool? Enabled { get; init; }
    public int? MaxInlineComments { get; init; }
    public int? MaxRetries { get; init; }
    public bool? OrderBySeverity { get; init; }
    public FindingSeverity? SeverityThreshold { get; init; }
}
