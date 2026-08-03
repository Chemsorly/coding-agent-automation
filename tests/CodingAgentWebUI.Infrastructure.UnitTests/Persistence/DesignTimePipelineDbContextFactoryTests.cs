using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="DesignTimePipelineDbContextFactory"/>.
/// Verifies that the factory throws when DESIGN_TIME_CONNECTION_STRING is missing,
/// and succeeds when a valid connection string is supplied.
/// </summary>
public class DesignTimePipelineDbContextFactoryTests : IDisposable
{
    private const string EnvVar = "DESIGN_TIME_CONNECTION_STRING";
    private readonly string? _originalValue;

    public DesignTimePipelineDbContextFactoryTests()
    {
        _originalValue = Environment.GetEnvironmentVariable(EnvVar);
        // Start each test with the variable cleared
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    public void Dispose()
    {
        // Restore to avoid polluting other tests
        Environment.SetEnvironmentVariable(EnvVar, _originalValue);
    }

    [Fact]
    public void CreateDbContext_EnvVarNotSet_ThrowsInvalidOperationException()
    {
        var factory = new DesignTimePipelineDbContextFactory();

        var act = () => factory.CreateDbContext([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DESIGN_TIME_CONNECTION_STRING*");
    }

    [Fact]
    public void CreateDbContext_EnvVarEmpty_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");
        var factory = new DesignTimePipelineDbContextFactory();

        var act = () => factory.CreateDbContext([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DESIGN_TIME_CONNECTION_STRING*");
    }

    [Fact]
    public void CreateDbContext_EnvVarWhitespaceOnly_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "   ");
        var factory = new DesignTimePipelineDbContextFactory();

        var act = () => factory.CreateDbContext([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DESIGN_TIME_CONNECTION_STRING*");
    }

    [Fact]
    public void CreateDbContext_ValidConnectionString_ReturnsContext()
    {
        Environment.SetEnvironmentVariable(EnvVar, "Host=localhost;Database=test;Username=u;Password=p");
        var factory = new DesignTimePipelineDbContextFactory();

        using var ctx = factory.CreateDbContext([]);

        ctx.Should().NotBeNull();
    }

    [Fact]
    public void CreateDbContext_ExceptionMessageIncludesExampleConnectionString()
    {
        var factory = new DesignTimePipelineDbContextFactory();

        var act = () => factory.CreateDbContext([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Host=localhost*");
    }
}
