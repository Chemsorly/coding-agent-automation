using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Groups the core dependencies of <see cref="AgentHub"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentHubDependencies(
    IAgentHubFacade Facade,
    IChatNotifier ChatNotifier,
    IChangeNotifier ChangeNotifier,
    ModelFetchService ModelFetchService,
    IConsolidationService ConsolidationService,
    ConsolidationBadgeService BadgeService,
    IHubIssueOperations IssueOps,
    IAgentJobLifecycleService LifecycleService,
    IAgentTokenRefreshService TokenRefreshService,
    IGateCommentFormatter GateCommentFormatter,
    ILogger Logger,
    IAgentOrphanRecoveryService OrphanRecoveryService,
    IHubContext<AgentHub> UiContext);
