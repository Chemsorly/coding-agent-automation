using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Groups the core dependencies of <see cref="AgentHub"/> to reduce
/// constructor parameter count (S107). All members are required.
/// T10 (arch-audit 2026-08-22): Consolidation cluster extracted into
/// <see cref="IHubConsolidationOperations"/> — record reduced from 13 to 10 members.
/// </summary>
public sealed record AgentHubDependencies(
    IAgentHubFacade Facade,
    IChatNotifier ChatNotifier,
    IChangeNotifier ChangeNotifier,
    IHubConsolidationOperations ConsolidationOps,
    IHubIssueOperations IssueOps,
    IAgentJobLifecycleService LifecycleService,
    IAgentTokenRefreshService TokenRefreshService,
    IGateCommentFormatter GateCommentFormatter,
    ILogger Logger,
    IAgentOrphanRecoveryService OrphanRecoveryService,
    IHubContext<AgentHub> UiContext);
