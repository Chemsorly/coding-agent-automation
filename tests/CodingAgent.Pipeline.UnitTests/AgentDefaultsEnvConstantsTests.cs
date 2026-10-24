using AwesomeAssertions;
using CodingAgent.Pipeline;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="AgentDefaults"/> exposes the correct environment-variable name
/// constants for SERVICE_VERSION and OTEL_SERVICE_NAME, so every caller that replaces a
/// string literal with the constant reads the same variable.
/// </summary>
public class AgentDefaultsEnvConstantsTests
{
    [Fact]
    public void EnvServiceVersion_HasCorrectValue()
    {
        AgentDefaults.EnvServiceVersion.Should().Be("SERVICE_VERSION");
    }

    [Fact]
    public void EnvOtelServiceName_HasCorrectValue()
    {
        AgentDefaults.EnvOtelServiceName.Should().Be("OTEL_SERVICE_NAME");
    }

    [Fact]
    public void EnvLogLevel_HasCorrectValue()
    {
        // Regression guard: the existing constant must not be changed.
        AgentDefaults.EnvLogLevel.Should().Be("LOG_LEVEL");
    }
}
