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
