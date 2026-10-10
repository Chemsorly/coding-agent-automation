# Issue Operations Proxy — SignalR Method Reference

Internal reference for how agent containers proxy issue operations through the orchestrator.

## Design

Agents do NOT receive `IIssueProvider` credentials directly. All issue operations are proxied through SignalR to the orchestrator, which resolves the `IIssueProvider` from the run's `IssueProviderConfigId`.

## Method Mapping

| Operation | Agent Method | Hub Method |
|-----------|-------------|------------|
| Create issue | `CreateIssueAsync` | `RequestCreateIssue` |
| Create issue (cross-provider) | `CreateIssueForProviderAsync` | `RequestCreateIssueForProvider` |
| List open issues | `ListOpenIssuesAsync` | `RequestListOpenIssues` |
| List closed issues | `ListClosedIssuesAsync` | `RequestListClosedIssues` |
| List open issues (cross-provider) | `ListOpenIssuesForProviderAsync` | `RequestListOpenIssuesForProvider` |
| List closed issues (cross-provider) | `ListClosedIssuesForProviderAsync` | `RequestListClosedIssuesForProvider` |
| Get issue details | `GetIssueAsync` | `RequestGetIssue` |
| List comments | `ListCommentsAsync` | `RequestListComments` |
| Update comment | `UpdateCommentAsync` | `RequestUpdateComment` |
| Post comment | `PostCommentAsync` | `RequestPostComment` |
| Change labels | `SwapLabelAsync` | `RequestLabelChange` |
| Refresh token | `RequestTokenRefreshAsync` | `RequestTokenRefresh` |
| Report a triage result | `ReportTriageResultAsync` | `ReportTriageResult` |

The cross-provider methods, like `RequestCreateIssueForProvider`, resolve the provider from the config ID the agent passes. The hub accepts a tracker other than the run's own only for a project epic's decomposition run, and, for listing issues only, for a triage run (any enabled tracker of its project).

Triage runs never create issues: the hub refuses `RequestCreateIssue` and `RequestCreateIssueForProvider` for them, because issues are created from the reviewed drafts in the app. `ReportTriageResult` ([RequiresActiveJob]) stores the result for the caller's own Triage WorkItem: the triage's key, project and source come from the WorkItem record, never from the agent, and a non-triage run is refused. An operator triage (`triage:{id}` on the `triage` tracker) has no issue, so its label and comment operations are skipped.

This keeps the agent's credential surface minimal — private keys and tokens never leave the orchestrator container.
