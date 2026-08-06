using System.Reflection;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Architecture;

/// <summary>
/// Enforces issue #1759: the six named interfaces must have zero 'string agentId' parameters.
/// This test permanently prevents regression by reflectively verifying that no method
/// in the specified interfaces declares a parameter of type <see cref="System.String"/>
/// named <c>agentId</c>.
/// </summary>
/// <remarks>
/// The six interfaces are tested individually by type reference rather than by scanning a file,
/// because <see cref="IAgentHubClient"/> lives in the same file as <see cref="IAgentHub"/>
/// but intentionally retains <c>string agentId</c> for SignalR wire-format safety.
/// </remarks>
public class AgentIdContractTests
{
    /// <summary>
    /// The six interfaces named in issue #1759 acceptance criteria.
    /// Each must have zero methods with a parameter of type <c>string</c> named <c>agentId</c>.
    /// </summary>
    private static readonly IReadOnlyList<Type> TargetInterfaces =
    [
        typeof(IAgentRegistryService),
        typeof(IAgentHubFacade),
        typeof(IAgentCommunication),
        typeof(ISignalRWorkDistributorAgentResolver),
        typeof(IAgentHub),
        typeof(IRunLifecycleManager)
    ];

    [Fact]
    public void TargetInterfaces_HaveNoStringAgentIdParameters()
    {
        var violations = new List<string>();

        foreach (var interfaceType in TargetInterfaces)
        {
            // TODO: BindingFlags.Instance has no meaningful effect on interface types — interface methods
            // are not instance members in the reflection sense. BindingFlags.Public alone (or no flags) is
            // sufficient for GetMethods on an interface. This works today but is subtly incorrect and should
            // be updated to interfaceType.GetMethods() or BindingFlags.Public | BindingFlags.DeclaredOnly.
            foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var parameter in method.GetParameters())
                {
                    // TODO: This check matches on parameter *name* "agentId" in addition to type string.
                    // A regression that renames the parameter (e.g., to "id") while keeping type string
                    // would silently pass this test. For stronger coverage, consider also asserting that
                    // all parameters intended to carry agent identity are typed as AgentId (not just
                    // checking for the absence of string-typed "agentId" parameters).
                    if (parameter.ParameterType == typeof(string) &&
                        string.Equals(parameter.Name, "agentId", StringComparison.Ordinal))
                    {
                        violations.Add($"{interfaceType.Name}.{method.Name}({parameter.Name}: string)");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"The following interface methods still declare 'string agentId' — use AgentId value type instead " +
            $"(issue #1759):{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations.Select(v => $"  \u2022 {v}")));
    }

    [Fact]
    public void IAgentHubFacade_IsFromWebUIProject()
    {
        // Verify IAgentHubFacade resolves from the WebUI assembly (not a stale reference).
        // This ensures the architecture test covers the correct type.
        Assert.Equal("CodingAgentWebUI", typeof(IAgentHubFacade).Assembly.GetName().Name);
    }

    [Fact]
    public void AgentId_ImplicitConversionFromString_Works()
    {
        // Verifies the implicit operator is functional — call sites that pass string literals
        // to the newly-typed AgentId parameters will continue to compile and work correctly.
        AgentId id = "test-agent-123";
        Assert.Equal("test-agent-123", id.Value);
        Assert.Equal("test-agent-123", id.ToString());
    }

    [Fact]
    public void AgentId_ImplicitConversionFromNullOrEmpty_Throws()
    {
        // The implicit operator uses ArgumentException.ThrowIfNullOrEmpty — verify it rejects
        // null and empty strings so the type contract is enforced at conversion boundaries.
        // Empty string throws ArgumentException; null throws ArgumentNullException (a subclass).
        Assert.Throws<ArgumentException>(() => { AgentId _ = ""; });
        Assert.ThrowsAny<ArgumentException>(() => { AgentId _ = (string)null!; });
    }
}
