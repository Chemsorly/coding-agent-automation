using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// An issue or pull request of a template's providers that has a live work item, or one that ended
/// within the dedup cooldown. The drawers badge it: <see cref="WorkItemStatus.Pending"/> is queued,
/// <see cref="WorkItemStatus.Running"/> covers dispatched and running.
/// </summary>
public sealed record UiActiveIssue(string IssueIdentifier, string IssueProviderConfigId, WorkItemStatus Status);
