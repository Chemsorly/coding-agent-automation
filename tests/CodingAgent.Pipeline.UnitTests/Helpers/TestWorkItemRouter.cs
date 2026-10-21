using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Helpers;

/// <summary>
/// Spec 053 task 4.0: a <see cref="WorkItemRouter"/> over a test database. Once
/// <see cref="AgentRoutingTestData.SeedRoutingAsync"/> stored the default route, every task type routes to
/// <see cref="AgentRoutingTestData.Profile"/> on <see cref="AgentRoutingTestData.DefaultJobTemplate"/>; without it,
/// every request is refused with <c>no_route</c>.
/// </summary>
internal static class TestWorkItemRouter
{
    public static WorkItemRouter Create(
        IDbContextFactory<PipelineDbContext> dbFactory,
        JobTemplateStore? jobTemplates = null,
        IReadOnlyList<AgentProfile>? profiles = null,
        IReadOnlyList<PipelineJobTemplate>? templates = null)
    {
        var profileStore = new Mock<IAgentProfileStore>();
        profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles ?? [AgentRoutingTestData.Profile()]);
        var projectStore = new Mock<IProjectStore>();
        projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates ?? []);
        return new WorkItemRouter(
            profileStore.Object, new AgentRoutingStore(dbFactory), projectStore.Object,
            jobTemplates ?? AgentRoutingTestData.JobTemplates(), NullLogger<WorkItemRouter>.Instance);
    }
}
