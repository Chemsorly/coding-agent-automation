using CodingAgentWebUI.Pipeline.CodeReview.Models;
using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

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

    // Key(3) retired: was ReviewIsolation enum (Shared/Isolated).
    // All review agents now unconditionally run in isolated sessions.
    // Slot kept reserved for MessagePack backward compatibility with old payloads.
}
