# Issue Dependency Tracking — Implementation Details

Internal reference for the dependency tracking mechanism in the dispatch loop.

## Regex Pattern

```
\b(?:blocked by|depends on|requires|after)\s+#(\d+)
```

Case-insensitive, word-boundary matched before the keyword.

## Stateless Body-Parsed Check

The dependency check runs fresh on each poll cycle (~30s default interval):

1. When a candidate issue is dequeued for dispatch, `DependencyParser` extracts issue numbers from the body text
2. For each referenced issue number, `DependencyChecker` calls `IsIssueClosedAsync` on the issue provider
3. Results are cached per-cycle in a shared `Dictionary<int, bool>` — if multiple candidates reference the same dependency, only one API call is made
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
| Same-repository only | Only `#N` references supported, not `owner/repo#N` |
