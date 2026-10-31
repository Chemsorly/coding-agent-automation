namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The provider refused a file write because the file or the branch changed meanwhile. The caller may retry:
/// a retry reads the file's current state again.
/// </summary>
public sealed class RemoteFileConflictException : InvalidOperationException
{
    public RemoteFileConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
