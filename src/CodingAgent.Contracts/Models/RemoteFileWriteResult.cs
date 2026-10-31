namespace CodingAgent.Pipeline.Models;

/// <summary>Result of writing one file through a provider's API.</summary>
/// <param name="CommitSha">The commit the provider created.</param>
public sealed record RemoteFileWriteResult(string CommitSha);
