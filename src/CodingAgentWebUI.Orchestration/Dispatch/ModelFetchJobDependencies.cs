using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the mandatory constructor dependencies of <see cref="ModelFetchJobService"/>
/// to reduce constructor parameter count (S107). Optional members are test-only overrides.
/// </summary>
public sealed record ModelFetchJobDependencies(
    IKubernetesJobClient KubeClient,
    JobTemplateStore TemplateStore,
    DispatchServiceOptions Options,
    IPipelineConfigStore ConfigStore,
    IModelFetchReceiver ModelFetchReceiver,
    int? PollTimeoutSecondsOverride = null,
    int PollIntervalMs = 2000,
    Serilog.ILogger? Logger = null);
