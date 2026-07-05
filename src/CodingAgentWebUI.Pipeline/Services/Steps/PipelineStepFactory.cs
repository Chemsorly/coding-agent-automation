namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Single source of truth for the shared core pipeline step sequences.
/// Both <see cref="PipelineOrchestrationService"/> and <c>LocalPipelineExecutor</c>
/// use this factory to avoid drift in the step ordering.
/// </summary>
public static class PipelineStepFactory
{
    /// <summary>
    /// Returns the core implementation step sequence shared by all implementation pipelines.
    /// Callers prepend their environment-specific prefix (FetchIssue, Clone, WriteMcpConfig, etc.)
    /// before these steps.
    /// </summary>
    /// <remarks>
    /// Order: DetectRework → WritePrConversationContext → CreateBranch → VerifyBaseline →
    /// AnalyzeCode → GenerateCode → BrainPullBeforeWrite → ReviewCode → RunQualityGates.
    /// </remarks>
    public static IReadOnlyList<IPipelineStep> CreateCoreImplementationSteps()
    {
        return new IPipelineStep[]
        {
            new DetectReworkStep(),
            new WritePrConversationContextStep(),
            new CreateBranchStep(),
            new VerifyBaselineStep(),
            new AnalyzeCodeStep(),
            new GenerateCodeStep(),
            new BrainPullBeforeWriteStep(),
            new ReviewCodeStep(),
            new RunQualityGatesStep()
        };
    }
}
