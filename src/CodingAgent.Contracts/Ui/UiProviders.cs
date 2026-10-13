namespace CodingAgent.Contracts.Ui;

/// <summary>
/// The outcome of a provider operation in Settings (credential check, Actions access, initialize):
/// whether it worked, and the sentence to show either way.
/// </summary>
public sealed record UiProviderCheck(bool Success, string Message);

/// <summary>A repository a GitHub App installation can access.</summary>
public sealed record UiRepository(string FullName, string Owner, string Name);
