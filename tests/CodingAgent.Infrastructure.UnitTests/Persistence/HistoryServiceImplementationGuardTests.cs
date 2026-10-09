using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

public class HistoryServiceImplementationGuardTests
{
    [Fact]
    public void PersistenceAssembly_ContainsOnlyPostgresHistoryService()
    {
        var types = typeof(PostgresPipelineRunHistoryService).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IPipelineRunHistoryService).IsAssignableFrom(t))
            .ToList();

        types.Should().ContainSingle("the file-backed history service was removed")
            .Which.Should().Be(typeof(PostgresPipelineRunHistoryService));
    }
}
