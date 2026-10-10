// TODO: namespace mismatch — this file lives in src/CodingAgent.Contracts/ but declares
//       namespace CodingAgent.Pipeline.Models (matching the existing convention in that project,
//       not the csproj name). This is consistent with all other types in CodingAgent.Contracts
//       (e.g. ProviderConfig.cs), so it is not a defect, but the divergence between project name
//       and namespace root may cause discoverability confusion for future consumers.
//       Review whether the established namespace root for CodingAgent.Contracts is intended to
//       remain CodingAgent.Pipeline.Models or should be aligned to CodingAgent.Contracts.Models.
//       Tracked by correctness/dotnet-specialist review finding (WARNING).
namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Provider-type discriminator string constants used in <see cref="ProviderConfig.ProviderType"/>.
/// Centralizes these string literals to prevent runtime failures from casing/typo drift.
/// </summary>
public static class ProviderTypes
{
    public const string GitHub = "GitHub";
    public const string GitLab = "GitLab";
    public const string KiroCli = "KiroCli";
    public const string OpenCode = "OpenCode";
    public const string ClaudeCode = "ClaudeCode";
}
