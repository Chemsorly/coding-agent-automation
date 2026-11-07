using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Helpers;

/// <summary>
/// Spec 056: a <see cref="DispatchRouter"/> over a test database. Once the routing table is stored
/// (<see cref="AgentRoutingTestData.SeedRoutingAsync"/>), every task type routes to one profile on
/// <paramref name="jobTemplateKey"/> (by default the first key of the job template store); without it, every item is
/// held with <c>no_route</c>.
/// </summary>
internal static class TestDispatchRouter
{
    public static DispatchRouter Create(
        IDbContextFactory<PipelineDbContext> dbFactory,
        JobTemplateStore? jobTemplates = null,
        IReadOnlyList<AgentProfile>? profiles = null,
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        string? jobTemplateKey = null,
        CredentialPauseStore? pauses = null,
        IOrchestratorRunService? runs = null)
    {
        var store = jobTemplates ?? AgentRoutingTestData.JobTemplates();
        var key = jobTemplateKey ?? store.ToolsByKey().Keys.First();
        var profileStore = new Mock<IAgentProfileStore>();
        profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles ?? [AgentRoutingTestData.Profile(jobTemplate: key)]);
        var projectStore = new Mock<IProjectStore>();
        projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates ?? []);
        return new DispatchRouter(
            dbFactory, profileStore.Object, new AgentRoutingStore(dbFactory), projectStore.Object, store,
            pauses ?? Pauses(dbFactory), runs ?? new Mock<IOrchestratorRunService>().Object, TimeProvider.System,
            Serilog.Core.Logger.None);
    }

    /// <summary>A pause store over <paramref name="dbFactory"/> with an in-process lock.</summary>
    public static CredentialPauseStore Pauses(IDbContextFactory<PipelineDbContext> dbFactory, TimeProvider? time = null) =>
        new(dbFactory, new SemaphoreLocks(), time ?? TimeProvider.System, Serilog.Core.Logger.None);

    private sealed class SemaphoreLocks : IDistributedLockProvider
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            return new Release(_gate);
        }

        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Creates the router and stores the default routing table in <paramref name="dbFactory"/>.</summary>
    public static async Task<DispatchRouter> CreateRoutedAsync(
        IDbContextFactory<PipelineDbContext> dbFactory, JobTemplateStore? jobTemplates = null, string? jobTemplateKey = null)
    {
        await AgentRoutingTestData.SeedRoutingAsync(dbFactory);
        return Create(dbFactory, jobTemplates, jobTemplateKey: jobTemplateKey);
    }
}
