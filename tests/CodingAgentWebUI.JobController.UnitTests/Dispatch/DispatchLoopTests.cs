// DispatchLoopTests have been removed as part of issue #2322.
// DispatchLoop and DispatchService have been deleted. The new synchronous dispatch path
// (POST /api/work-items/dispatch) is tested in:
// - tests/CodingAgentWebUI.UnitTests/Dispatch/KubernetesWorkDistributorDispatchTests.cs
// - tests/CodingAgentWebUI.Api.IntegrationTests/WorkItemEndpointTests.cs (DispatchWorkItem tests)
namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;
