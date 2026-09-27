// TODO [WARNING]: Namespace mismatch — this file is physically in src/CodingAgent.Contracts/Models/ but
// declares namespace CodingAgent.Pipeline.Models. The convention used by WorkspacePath, JobId, RunId
// and other types in this directory is CodingAgent.Pipeline.Models (the Contracts project shares that
// namespace). Consumers expecting CodingAgent.Contracts.Models will not find this type without an
// additional using directive.
namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for Git branch names used in repository provider operations.
/// Prevents accidental transposition of string parameters (workspacePath vs branchName vs message).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="JobId"/>, <see cref="RunId"/>, and <see cref="ProviderConfigId"/>, this type
/// includes a reverse implicit conversion (<c>BranchName → string</c>). This deviation is
/// intentional: <see cref="BranchName"/> is passed to numerous downstream APIs that accept
/// <c>string</c> (e.g., <c>RepositoryGitOperations</c>), and requiring <c>.Value</c> at every
/// call site would add excessive churn for a process-local type that is never serialized over
/// the wire.
/// </para>
/// <para>
/// <c>default(BranchName)</c> has <c>Value = null</c> because struct defaults bypass constructors
/// and operators. Implementation methods guard against this with
/// <c>ArgumentException.ThrowIfNullOrEmpty(branchName.Value)</c>.
/// </para>
/// </remarks>
// TODO [WARNING]: The primary constructor BranchName(string Value) is publicly accessible and bypasses
// the null-or-empty guard that lives only in the implicit string → BranchName operator. Callers writing
// new BranchName(null) or new BranchName("") will produce a BranchName with a null/empty Value without
// any exception. Consider adding validation in an explicit constructor body, or making the parameterised
// constructor private and routing all construction through the implicit operator. WorkspacePath has the
// same gap — if this is an intentional design match, document it here.
// TODO [WARNING]: The BranchName → string implicit operator returns branchName.Value without a null
// guard. A default(BranchName) flowing into a string-accepting API will silently pass null, potentially
// causing a NullReferenceException deeper in the call stack instead of failing at the conversion point.
// Adding ArgumentException.ThrowIfNullOrEmpty(branchName.Value) here would make the type safe in both
// directions. WorkspacePath has the same gap, so this may be an intentional pattern match.
public readonly record struct BranchName(string Value)
{
    public static implicit operator BranchName(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public static implicit operator string(BranchName branchName) => branchName.Value;

    public override string ToString() => Value;
}
