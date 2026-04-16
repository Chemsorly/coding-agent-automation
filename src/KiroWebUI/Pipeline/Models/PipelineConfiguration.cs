namespace KiroWebUI.Pipeline.Models;

public sealed class PipelineConfiguration
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double MinCoverageThreshold { get; init; } = 80.0;
    public bool SecurityScanEnabled { get; init; } = false;
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
    public bool SelfReviewEnabled { get; init; } = false;
    public int SelfReviewMaxIterations { get; init; } = 1;
    public string SelfReviewPrompt { get; init; } =
        "Use a sub-agent to review the changes you just made against the original issue requirements. " +
        "The sub-agent should check for: correctness against acceptance criteria, code quality and " +
        "project conventions, unhandled edge cases, and security gaps. Fix any issues the review finds.";
}
