namespace KiroCliLib.Core;

/// <summary>
/// Minimal workspace path constants used by the Kiro CLI library.
///
/// These constants mirror the values in <c>AgentWorkspacePaths</c> (CodingAgent.Contracts),
/// but are defined here independently to avoid a circular project reference:
/// CodingAgent.Contracts already references KiroCliLib, so KiroCliLib cannot reference
/// CodingAgent.Contracts without creating a build-breaking cycle.
/// If the path values ever change, update both this class and AgentWorkspacePaths together.
/// </summary>
// TODO: The sync obligation stated in this class's comment ("update both ... together") is only
// enforced by KiroCliWorkspacePathsTests for MetadataDirectory. The test pins the literal ".agent"
// rather than comparing against AgentWorkspacePaths.MetadataDirectory directly (which would require
// a circular reference). If AgentWorkspacePaths.MetadataDirectory is ever changed, the test will
// still pass, leaving the two constants silently out of sync. If the circular dependency is ever
// resolved (e.g., by extracting a shared primitives assembly), update the test to compare both
// constants directly rather than pinning against a hardcoded string.
internal static class KiroCliWorkspacePaths
{
    /// <summary>
    /// The root metadata directory inside target workspaces. Mirrors AgentWorkspacePaths.MetadataDirectory.
    /// </summary>
    internal const string MetadataDirectory = ".agent";
}
