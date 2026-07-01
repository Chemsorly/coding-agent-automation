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
| Get issue details | `GetIssueAsync` | `RequestGetIssue` |
| List comments | `ListCommentsAsync` | `RequestListComments` |
| Update comment | `UpdateCommentAsync` | `RequestUpdateComment` |
| Post comment | `PostCommentAsync` | `RequestPostComment` |
| Change labels | `ChangeLabelAsync` | `RequestLabelChange` |
| Refresh token | `RefreshTokenAsync` | `RequestTokenRefresh` |

This keeps the agent's credential surface minimal — private keys and tokens never leave the orchestrator container.
