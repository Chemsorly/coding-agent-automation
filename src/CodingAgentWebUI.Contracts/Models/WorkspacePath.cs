namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for workspace directory paths used in repository provider operations.
/// Prevents accidental transposition of string parameters (workspacePath vs branchName vs message).
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="JobId"/>, <see cref="RunId"/>, and <see cref="ProviderConfigId"/>, this type
/// includes a reverse implicit conversion (<c>WorkspacePath → string</c>). This deviation is
/// intentional: <see cref="WorkspacePath"/> is passed to numerous downstream APIs that accept
/// <c>string</c> (e.g., <c>RepositoryGitOperations</c>, <c>Directory.CreateDirectory</c>), and
/// requiring <c>.Value</c> at every call site would add excessive churn for a process-local type
/// that is never serialized over the wire.
/// </para>
/// <para>
/// <c>default(WorkspacePath)</c> has <c>Value = null</c> because struct defaults bypass constructors
/// and operators. Implementation methods guard against this with
/// <c>ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value)</c>.
/// </para>
/// </remarks>
public readonly record struct WorkspacePath(string Value)
{
    public static implicit operator WorkspacePath(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public static implicit operator string(WorkspacePath path) => path.Value;

    public override string ToString() => Value;
}
