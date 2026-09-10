// DispatchServiceTests have been removed as part of issue #2322.
// DispatchLoop and DispatchService have been deleted. The new synchronous dispatch path
// (POST /api/work-items/dispatch) is tested in:
// - tests/CodingAgent.Web.UnitTests/Dispatch/KubernetesWorkDistributorDispatchTests.cs
// - tests/CodingAgent.Api.IntegrationTests/WorkItemEndpointTests.cs (DispatchWorkItem tests)
namespace CodingAgent.JobController.UnitTests.Dispatch;
