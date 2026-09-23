using AwesomeAssertions;
using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Pins the value of <see cref="ProcessWrapper.AgentMetadataDirectory"/> against accidental drift.
///
/// <para>
/// <see cref="ProcessWrapper"/> cannot reference <c>CodingAgent.Contracts</c> due to a circular
/// dependency (<c>CodingAgent.Contracts</c> → <c>KiroCliLib</c>), so it holds a local mirror
/// constant that must stay in sync with <c>AgentWorkspacePaths.MetadataDirectory</c>.
/// This test pins that value so that any future change to the constant is caught immediately
/// rather than silently diverging from the canonical definition.
/// </para>
///
/// <para>
/// If this test fails after a directory rename, update both
/// <c>AgentWorkspacePaths.MetadataDirectory</c> in <c>CodingAgent.Contracts</c> and
/// <c>ProcessWrapper.AgentMetadataDirectory</c> in <c>KiroCliLib</c> together.
/// </para>
/// </summary>
public class ProcessWrapperConstantsTests
{
    [Fact]
    public void AgentMetadataDirectory_MatchesKnownValue()
    {
        // This pins the string value of the local mirror constant.
        // It must stay in sync with AgentWorkspacePaths.MetadataDirectory in CodingAgent.Contracts.
        // Cannot reference AgentWorkspacePaths directly due to circular dependency: Contracts → KiroCliLib.
        ProcessWrapper.AgentMetadataDirectory.Should().Be(".agent",
            "ProcessWrapper.AgentMetadataDirectory must mirror AgentWorkspacePaths.MetadataDirectory; " +
            "update both constants together when the directory name changes");
    }
}
