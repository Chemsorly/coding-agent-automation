# Issue Dependency Tracking — Implementation Details

Internal reference for the dependency tracking mechanism in the dispatch loop.

## Regex Pattern

```
\b(?:blocked\s+by|depends\s+on|requires|after)\s+(?:(https://github\.com/[^/\s]+/[^/\s]+/issues/\d+)|(https://gitlab\.com/[^/\s]+/[^/\s]+/-/issues/\d+)|#(\d+)|([A-Za-z][\w-]*))
```

Case-insensitive, word-boundary matched before the keyword. Supports four reference formats:
- Group 1 — GitHub issue URL (`https://github.com/owner/repo/issues/N`)
- Group 2 — GitLab issue URL (`https://gitlab.com/group/project/-/issues/N`, one namespace level)
- Group 3 — `#N`
- Group 4 — alphanumeric identifier starting with a letter (e.g., `PROJ-123`)

`#N` references and issue URLs on `https://github.com` and `https://gitlab.com` are used for dispatch blocking; alphanumeric identifiers (group 4) are ignored. A URL is checked in the configured tracker whose URL prefix it matches; a URL that matches no configured tracker counts as unresolved. URLs on other hosts (GitHub Enterprise, self-hosted GitLab) match no URL group and do not block dispatch.

Self-references are excluded via the optional `selfIdentifier` parameter.

## Stateless Body-Parsed Check

The dependency check runs fresh on each poll cycle (`ClosedLoopPollInterval`, default 60 s):

1. When a candidate issue is dequeued for dispatch, `DependencyParser` extracts issue numbers from the body text
2. For each referenced issue number, `DependencyChecker` calls `IsIssueClosedAsync` on the issue provider
3. Results are cached per cycle, one `Dictionary<int, bool>` per tracker: if several candidates of a tracker reference the same dependency, only one API call is made. Issue numbers are unique only within a tracker, so #12 is resolved in the dependent issue's own tracker and an answer about #12 in one tracker is never reused for another
4. If ALL dependencies are closed → issue is eligible for dispatch
5. If ANY dependency is still open → issue is skipped

No internal state persisted between cycles. No new labels introduced.

## Behavior When Dependencies Are Unresolved

- Issue stays labeled `agent:next` (no label change)
- Skipped silently from dispatch this cycle
- Next poll cycle, check runs again
- A blocked issue at the front of the queue does NOT prevent unblocked issues behind it from being dispatched

## Graceful Degradation

- **API failures** (network errors, rate limits, 5xx after retry exhaustion) → dependency treated as unresolved → issue skipped, warning logged
- **Issue not found** (404) → treated as unresolved (conservative)
- **Null body** → treated as no dependencies (issue dispatches normally)
- Failures for one issue do NOT affect others in the same cycle
- Failures do NOT cause poll cycle abort, circuit breaker trip, or template failure

## Known Limitations

| Limitation | Description |
|------------|-------------|
| "After" false positives | Common English word matches. Prefer `Blocked by` or `Depends on`. |
| Code block matching | Patterns inside markdown code blocks still match |
| Strikethrough matching | `~~Blocked by #123~~` still matches |
| No circular dependency detection | A depends on B and B depends on A = both skipped indefinitely |
| Cross-tracker by URL only | `#N` is resolved in the dependent issue's own tracker. Another tracker's issue needs its full `https://github.com` or `https://gitlab.com` issue URL; `owner/repo#N`, URLs on other hosts and GitLab URLs with nested subgroups are not recognized and do not block dispatch |
