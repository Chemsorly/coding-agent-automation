// DispatchService has been removed as part of issue #2322.
// Regular work item dispatch (DispatchLoop) has been eliminated:
// KubernetesWorkDistributor now calls POST /api/work-items/dispatch directly,
// which atomically creates the K8s Job and transitions the WorkItem to Dispatched
// without passing through the Pending queue.
// ConsolidationDispatchService (for consolidation work items) was removed in issue #2323
// for the same reason — it was already a no-op in production.
namespace CodingAgent.JobController.Dispatch;
