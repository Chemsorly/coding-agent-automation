namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Factory for constructing shared pipeline step sequences. Provides a single source of truth
/// for the core implementation steps used by orchestrator-side, agent-side, and test execution.
/// </summary>
public static class PipelineStepFactory
{
    /// <summary>
    /// Returns the core implementation pipeline steps shared between all execution paths.
    /// Each call returns a new mutable list so callers can prepend/append environment-specific steps.
    /// </summary>
    public static List<IPipelineStep> CreateImplementationCoreSteps()
    {
        return
        [
            new DetectReworkStep(),
            new WritePrConversationContextStep(),
            new CreateBranchStep(),
            new VerifyBaselineStep(),
            new AnalyzeCodeStep(),
            new GenerateCodeStep(),
            new BrainPullBeforeWriteStep(),
            new ReviewCodeStep(),
            new RunQualityGatesStep()
        ];
    }
}
