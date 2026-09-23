using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Groups the core dependencies of <see cref="AgentJobLifecycleService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
// TODO: [WARNING] This record is declared public because AgentJobLifecycleService (also public)
// accepts it as its sole constructor parameter, and external callers must be able to construct it.
// However, the record exposes Serilog.ILogger as a positional property in the public API surface,
// forcing any external caller to take a hard dependency on Serilog — inconsistent with the assembly
// convention of keeping Serilog-carrying types internal (e.g. AgentIdleTransitioner, StepMetadataApplier).
// Consider making this record internal and exposing only a factory/extension method (e.g. in
// AgentHubServiceCollectionExtensions) to construct AgentJobLifecycleService without leaking
// Serilog into the public API. (DotNetSpecialist review finding.)
public sealed record AgentJobLifecycleServiceDependencies(
    IAgentHubFacade Facade,
    IRunLifecycleManager LifecycleManager,
    ILabelService LabelService,
    IHubIssueOperations IssueOps,
    IChangeNotifier ChangeNotifier,
    IHostApplicationLifetime AppLifetime,
    IFeedbackCommentOutbox Outbox,
    ILogger Logger);
