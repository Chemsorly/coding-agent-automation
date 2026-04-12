namespace KiroWebUI.Pipeline.Models;

public sealed class PipelineConfiguration
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public double MinCoverageThreshold { get; init; } = 80.0;
    public bool SecurityScanEnabled { get; init; } = false;
    public string WorkspaceBaseDirectory { get; init; } = "./workspaces";
}
