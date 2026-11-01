namespace CodingAgent.Pipeline.Models;

/// <summary>The files a pull request changes, as the provider reports them.</summary>
/// <param name="Files">Changed paths. A rename lists both its old and its new path.</param>
/// <param name="IsComplete">False when the provider's list may have been cut short.</param>
/// <param name="HeadSha">The head commit the provider's list belongs to.</param>
public sealed record PullRequestChangedFiles(IReadOnlyList<string> Files, bool IsComplete, string HeadSha);
