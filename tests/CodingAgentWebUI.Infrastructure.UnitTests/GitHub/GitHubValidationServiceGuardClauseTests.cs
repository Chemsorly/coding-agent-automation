using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Tests for guard clauses in GitHubValidationService (Requirements 5.1–5.2).
/// </summary>
public class GitHubValidationServiceGuardClauseTests
{
    /// <summary>
    /// Requirement 5.1: Null providerFactory throws ArgumentNullException.
    /// </summary>
    [Fact]
    public void Constructor_NullProviderFactory_ThrowsArgumentNullException()
    {
        var act = () => new GitHubValidationService((IProviderFactory)null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("providerFactory");
    }

    /// <summary>
    /// Requirement 5.2: Parameterless constructor does not throw (valid usage).
    /// </summary>
    [Fact]
    public void ParameterlessConstructor_DoesNotThrow()
    {
        var act = () => new GitHubValidationService();

        act.Should().NotThrow();
    }
}
