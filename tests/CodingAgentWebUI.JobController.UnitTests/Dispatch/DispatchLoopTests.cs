// DispatchLoopTests have been removed as part of issue #2322.
// DispatchLoop and DispatchService have been deleted. The new synchronous dispatch path
// (POST /api/work-items/dispatch) is tested in:
// - tests/CodingAgentWebUI.UnitTests/Dispatch/KubernetesWorkDistributorDispatchTests.cs
// - tests/CodingAgentWebUI.Api.IntegrationTests/WorkItemEndpointTests.cs (DispatchWorkItem tests)
//
// TODO [WARNING] (issue #2323): DispatchLoopHelpers.cs (BuildConcurrencyMapAsync, IsJobTerminal,
// SelectAvailablePvcAsync) was also deleted in #2323 along with ConsolidationDispatchLoop and its
// tests. DispatchLoop was already a stub with no callers at the time of deletion (confirmed: grep
// found zero references), so no coverage gap was introduced. If a future dispatch helper with
// similar logic is introduced, ensure it has its own unit tests rather than relying on deletion
// of the shared helper being safe because no callers remain.
namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;
