namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// HTML comment markers and headers used to identify pipeline-generated comments on issues.
/// These markers are used for both emission (writing comments) and detection (finding existing comments).
/// </summary>
public static class CommentMarkers
{
    /// <summary>Header for the agent analysis comment posted on issues.</summary>
    public const string AnalysisHeader = "## 🤖 Agent Analysis";

    /// <summary>HTML marker indicating a gate rejection comment (issue needs refinement).</summary>
    public const string GateRejection = "<!-- agent:gate-rejection -->";

    /// <summary>HTML marker indicating a gate won't-do comment (agent declined the issue).</summary>
    public const string GateWontDo = "<!-- agent:gate-wont-do -->";

    /// <summary>HTML marker indicating an issue feedback comment.</summary>
    public const string IssueFeedback = "<!-- agent:issue-feedback -->";

    /// <summary>Emoji prefix shared by all pipeline-generated comment headers.</summary>
    public const string PipelinePrefix = "## 🤖";

    /// <summary>HTML comment prefix used to tag agent-generated comments for detection.</summary>
    public const string AgentCommentPrefix = "<!-- agent:";

    /// <summary>HTML marker for the decomposition plan comment.</summary>
    public const string DecompositionPlan = "<!-- agent:decomposition-plan -->";

    /// <summary>HTML marker for the decomposition summary comment.</summary>
    public const string DecompositionSummary = "<!-- agent:decomposition-summary -->";

    /// <summary>HTML marker for the PR review comment.</summary>
    public const string PrReview = "<!-- agent:pr-review -->";
}
