namespace CodingAgent.Pipeline.Models;

/// <summary>A brain repository commit identity (spec 054, task 2B.1).</summary>
public sealed record BrainCommitInfo(string Sha, string Message);
