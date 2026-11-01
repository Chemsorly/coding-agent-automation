using Xunit;

namespace CodingAgent.Web.E2ETests.Infrastructure.SystemTier;

/// <summary>
/// xUnit collection definition for the System E2E tier. All tests in this collection
/// share a single <see cref="SystemContainers"/> instance (one Postgres + one Redis
/// container pair per test run), started once and reused across tests.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SystemE2ECollection : ICollectionFixture<SystemContainers>
{
    public const string Name = "System E2E";
}
