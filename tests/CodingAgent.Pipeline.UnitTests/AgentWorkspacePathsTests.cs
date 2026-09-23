using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Pins the string values of all <see cref="AgentWorkspacePaths"/> constants.
/// A typo on a constant silently breaks every call site that uses it, so these
/// assertions act as a regression guard that the compiler alone cannot provide.
/// </summary>
public class AgentWorkspacePathsTests
{
    // TODO: Add an assembly-provenance test asserting
    // typeof(AgentWorkspacePaths).Assembly.GetName().Name == "CodingAgent.Contracts"
    // so that a future move back to CodingAgent.Pipeline is caught immediately.
    // Currently, all existing tests pass whether the class resides in either assembly.

    // TODO: Add a source-scan test that enumerates all .cs files and asserts that no
    // file outside CodingAgent.Contracts/Models/AgentWorkspacePaths.cs and
    // KiroCliLib/Core/ProcessWrapper.cs contains a raw ".agent" or ".brain" string literal.
    // Without this guard, the regression the issue was designed to prevent can silently recur.
    // TODO: Consider renaming to MetadataDirectory_Value_IsAgentDirectory — the current name "_IsAgentDot"
    // is ambiguous: it reads as if the value ends with a dot rather than begins with one, and is
    // asymmetric with BrainDirectory_IsBrainDot. Clarifying the name would reduce maintenance misreads.
    [Fact]
    public void MetadataDirectory_IsAgentDot()
    {
        AgentWorkspacePaths.MetadataDirectory.Should().Be(".agent");
    }

    // TODO: This test pins the constant's raw string value but does not verify that consumers
    // (e.g. BrainSyncService) use the constant rather than a residual hardcoded literal. Review
    // BrainSyncPropertyTests.cs to confirm the brainPath construction path is exercised end-to-end,
    // so a future accidental reintroduction of a hardcoded ".brain" literal would be caught there.
    [Fact]
    public void BrainDirectory_IsBrainDot()
    {
        AgentWorkspacePaths.BrainDirectory.Should().Be(".brain");
    }
}
