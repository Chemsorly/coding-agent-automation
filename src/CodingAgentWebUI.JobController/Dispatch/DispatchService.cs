// DispatchService has been removed as part of issue #2322.
// Regular work item dispatch (DispatchLoop) has been eliminated:
// KubernetesWorkDistributor now calls POST /api/work-items/dispatch directly,
// which atomically creates the K8s Job and transitions the WorkItem to Dispatched
// without passing through the Pending queue.
// ConsolidationDispatchService (for consolidation work items) remains in place.
namespace CodingAgentWebUI.JobController.Dispatch;
