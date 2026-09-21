using AwesomeAssertions;
using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Pins the string value of <see cref="KiroCliWorkspacePaths.MetadataDirectory"/>.
///
/// This constant mirrors <c>AgentWorkspacePaths.MetadataDirectory</c> in CodingAgent.Contracts.
/// The two constants must stay in sync; if either value changes, BOTH must be updated together.
/// This test prevents silent drift between the two definitions.
/// </summary>
// TODO: This test pins KiroCliWorkspacePaths.MetadataDirectory against a hardcoded string ".agent"
// rather than against AgentWorkspacePaths.MetadataDirectory directly. This means the test will NOT
// catch drift if AgentWorkspacePaths.MetadataDirectory is changed without updating this constant —
// both sides of the drift would need to be updated before the test fails. The direct comparison is
// blocked by the circular project-reference constraint (CodingAgent.Contracts → KiroCliLib). If
// that constraint is resolved in the future, update this assertion to:
//   KiroCliWorkspacePaths.MetadataDirectory.Should().Be(AgentWorkspacePaths.MetadataDirectory)
// Additionally, no pin test exists for BrainDirectory; add one if KiroCliWorkspacePaths gains
// a BrainDirectory constant in the future.
public class KiroCliWorkspacePathsTests
{
    [Fact]
    public void MetadataDirectory_IsAgentDot()
    {
        KiroCliWorkspacePaths.MetadataDirectory.Should().Be(".agent");
    }
}
