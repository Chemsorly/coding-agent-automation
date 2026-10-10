using CodingAgent.Agent;
using CodingAgent.Pipeline;
using Xunit;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentProviderFactory.ResolveCliPath"/>.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class AgentProviderFactoryResolveCliPathTests
{
    private const string TestEnvVar = "RESOLVE_CLI_PATH_TEST_VAR";
    private const string DefaultPath = "/usr/local/bin/default-tool";

    [Fact]
    public void ResolveCliPath_VariableIsSet_ReturnsThatValue()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "/custom/path/tool");
            var result = AgentProviderFactory.ResolveCliPath(TestEnvVar, DefaultPath);
            Assert.Equal("/custom/path/tool", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsNotSet_ReturnsDefaultPath()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, null);
            var result = AgentProviderFactory.ResolveCliPath(TestEnvVar, DefaultPath);
            Assert.Equal(DefaultPath, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsEmptyString_ReturnsDefaultPath()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "");
            var result = AgentProviderFactory.ResolveCliPath(TestEnvVar, DefaultPath);
            Assert.Equal(DefaultPath, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }

    [Fact]
    public void ResolveCliPath_VariableIsWhitespace_ReturnsDefaultPath()
    {
        var original = Environment.GetEnvironmentVariable(TestEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(TestEnvVar, "   ");
            var result = AgentProviderFactory.ResolveCliPath(TestEnvVar, DefaultPath);
            Assert.Equal(DefaultPath, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestEnvVar, original);
        }
    }
}
