using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Groups the core dependencies of <see cref="OrphanedLabelRecoveryService"/> to reduce
/// constructor parameter count (S107). <see cref="GracePeriod"/> is optional (defaults to 60s).
/// </summary>
internal sealed record OrphanedLabelRecoveryServiceDependencies(
    IOrchestratorRunService RunService,
    IProjectStore ProjectStore,
    IProviderConfigStore ProviderConfigStore,
    IProviderFactory ProviderFactory,
    ILabelService LabelService,
    IPipelineConfigStore ConfigStore,
    ILogger Logger,
    TimeSpan GracePeriod = default);
