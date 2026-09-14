using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Verifies that <see cref="ApiServiceCollectionExtensions.AddApiInfrastructure"/> wires up
/// <c>NpgsqlRetryingExecutionStrategy</c> for the pooled <see cref="PipelineDbContext"/> factory.
///
/// <para>
/// <see cref="CodingAgent.Api.IntegrationTests.ApiWebApplicationFactory"/> replaces the real
/// Npgsql factory with an InMemory one, so verifying the retry strategy cannot be done via the
/// integration-test harness — this standalone unit test calls the DI registration directly.
/// </para>
/// </summary>
[Trait("Feature", "ConnectionResiliency")]
public sealed class ApiDbContextRetryStrategyTests
{
    /// <summary>
    /// After calling <see cref="ApiServiceCollectionExtensions.AddApiInfrastructure"/>, the
    /// resolved <see cref="IDbContextFactory{TContext}"/> must produce contexts whose execution
    /// strategy is <c>NpgsqlRetryingExecutionStrategy</c> (not <c>NonRetryingExecutionStrategy</c>).
    /// <para>
    /// <c>NpgsqlRetryingExecutionStrategy</c> is an internal Npgsql type, so we assert by type name
    /// rather than direct type reference to avoid coupling to internal APIs.
    /// </para>
    /// </summary>
    [Fact]
    public void AddApiInfrastructure_DbContextFactory_UsesNpgsqlRetryingExecutionStrategy()
    {
        // Arrange: build a minimal service collection with the real AddApiInfrastructure wiring.
        // A syntactically valid but unreachable connection string is sufficient — we only inspect
        // the execution strategy type; no actual database connection is made.
        var services = new ServiceCollection();
        const string testConnectionString = "Host=localhost;Database=test;Username=test;Password=test";

        // Act: register API infrastructure (this is the code under test)
        services.AddApiInfrastructure(testConnectionString);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        // TODO [WARNING]: factory.CreateDbContext() on a pooled factory backed by a real Npgsql
        // provider may attempt to validate or warm the connection pool in a future EF Core/Npgsql
        // version. Today EF Core defers connection opening to the first query and CreateExecutionStrategy()
        // needs no live connection, but this is an environment-dependent assumption. If this test
        // starts failing with a connection error against localhost, switch to a design-time or
        // in-memory approach to obtain the strategy type without opening a connection.
        // See Issue #2576 review findings (TestQualityReviewer [WARNING]).
        using var context = factory.CreateDbContext();

        var strategy = context.Database.CreateExecutionStrategy();
        var strategyTypeName = strategy.GetType().Name;

        // Assert: strategy must be the Npgsql retrying strategy, not the no-op fallback
        // NpgsqlRetryingExecutionStrategy is an internal Npgsql type; compare by name to avoid
        // coupling to an internal API while still providing a clear, actionable assertion.
        strategyTypeName.Should().Be("NpgsqlRetryingExecutionStrategy",
            "EnableRetryOnFailure must configure NpgsqlRetryingExecutionStrategy on the pooled factory " +
            "(Issue #2576 — transient Postgres blips must be retried at the ORM layer)");
    }
}
