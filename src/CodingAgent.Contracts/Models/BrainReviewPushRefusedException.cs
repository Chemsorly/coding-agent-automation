namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Thrown when a brain review push is refused due to a concurrent skill-file change on the remote
/// (spec 054, task 2B.1).
/// </summary>
public sealed class BrainReviewPushRefusedException : InvalidOperationException
{
    public const string SkillFileChangedRemotely = "skill_file_changed_remotely";

    public string Reason { get; }
    public string Path { get; }

    public BrainReviewPushRefusedException(string reason, string path)
        : base($"Brain review push refused: {reason} ({path})")
    {
        Reason = reason;
        Path = path;
    }
}
