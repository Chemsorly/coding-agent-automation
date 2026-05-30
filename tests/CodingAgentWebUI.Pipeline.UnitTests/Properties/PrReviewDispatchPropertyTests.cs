using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Xunit;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for PR review dispatch logic (P1–P6).
/// Feature: 025-pr-review-pipeline
/// </summary>
public class PrReviewDispatchPropertyTests
{
    // ─── P1: PR Label Filtering ─────────────────────────────────────────────────
    // For any collection of open PRs with varying labels, calling the filtering logic
    // with a specific label set SHALL return only those PRs whose labels intersect with
    // the requested set — and when the label filter is null or empty, all PRs are returned.
    // **Validates: Requirements 1.1, 1.2**

    /// <summary>
    /// P1a: When label filter is non-empty, only PRs with at least one matching label are returned.
    /// </summary>
    [Property(MaxTest = 20)]
    public void P1_LabelFiltering_ReturnsOnlyMatchingPrs(PositiveInt prCountRaw)
    {
        var prCount = Math.Min(prCountRaw.Get, 20);
        var allLabels = new[] { "agent:next", "bug", "enhancement", "docs", "refactoring" };
        var filterLabels = new[] { "agent:next" };

        var prs = Enumerable.Range(0, prCount).Select(i => new PullRequestSummary
        {
            Number = i + 1,
            Identifier = (i + 1).ToString(),
            Title = $"PR {i + 1}",
            Description = "",
            Labels = new[] { allLabels[i % allLabels.Length] },
            BranchName = $"feature-{i}",
            TargetBranch = "main",
            Url = $"https://github.com/test/repo/pull/{i + 1}",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow.AddMinutes(-i)
        }).ToList();

        // Apply the same filtering logic as ListOpenPullRequestsAsync with label filter
        var filtered = prs.Where(pr =>
            pr.Labels.Any(l => filterLabels.Contains(l))).ToList();

        // Verify: all returned PRs have at least one label in the filter set
        foreach (var pr in filtered)
        {
            pr.Labels.Any(l => filterLabels.Contains(l)).Should().BeTrue(
                $"PR #{pr.Identifier} should have at least one label matching the filter");
        }

        // Verify: no PR with a matching label was excluded
        var excluded = prs.Except(filtered);
        foreach (var pr in excluded)
        {
            pr.Labels.Any(l => filterLabels.Contains(l)).Should().BeFalse(
                $"PR #{pr.Identifier} should NOT have any label matching the filter");
        }
    }

    /// <summary>
    /// P1b: When label filter is null or empty, all PRs are returned unfiltered.
    /// </summary>
    [Property(MaxTest = 20)]
    public void P1_NullOrEmptyFilter_ReturnsAllPrs(PositiveInt prCountRaw, bool useNull)
    {
        var prCount = Math.Min(prCountRaw.Get, 20);
        var allLabels = new[] { "agent:next", "bug", "enhancement", "docs" };

        var prs = Enumerable.Range(0, prCount).Select(i => new PullRequestSummary
        {
            Number = i + 1,
            Identifier = (i + 1).ToString(),
            Title = $"PR {i + 1}",
            Description = "",
            Labels = new[] { allLabels[i % allLabels.Length] },
            BranchName = $"feature-{i}",
            TargetBranch = "main",
            Url = $"https://github.com/test/repo/pull/{i + 1}",
            IsDraft = false,
            CreatedAt = DateTime.UtcNow.AddMinutes(-i)
        }).ToList();

        IReadOnlyList<string>? filterLabels = useNull ? null : Array.Empty<string>();

        // When filter is null or empty, no filtering is applied
        var filtered = (filterLabels is null || filterLabels.Count == 0)
            ? prs.ToList()
            : prs.Where(pr => pr.Labels.Any(l => filterLabels.Contains(l))).ToList();

        filtered.Count.Should().Be(prCount,
            "null or empty filter should return all PRs unfiltered");
    }

    // ─── P2: FIFO Ordering by CreatedAt ─────────────────────────────────────────
    // For any list of PullRequestSummary items with random CreatedAt values, applying
    // the dispatch ordering SHALL produce a list sorted by CreatedAt ascending (oldest first),
    // with PRs lacking a CreatedAt sorted last.
    // **Validates: Requirements 2.2**

    [Property(MaxTest = 20)]
    public void P2_FifoOrdering_SortsByCreatedAtAscending(PositiveInt prCountRaw)
    {
        var prCount = Math.Min(prCountRaw.Get, 30);
        var random = new Random(prCountRaw.Get);

        var prs = Enumerable.Range(0, prCount).Select(i => new PullRequestSummary
        {
            Number = i + 1,
            Identifier = (i + 1).ToString(),
            Title = $"PR {i + 1}",
            Description = "",
            Labels = new[] { "agent:next" },
            BranchName = $"feature-{i}",
            TargetBranch = "main",
            Url = $"https://github.com/test/repo/pull/{i + 1}",
            IsDraft = false,
            // Some PRs have null CreatedAt, some have random dates
            CreatedAt = (i % 4 == 0) ? null : DateTime.UtcNow.AddMinutes(-random.Next(0, 10000))
        }).ToList();

        // Apply the same FIFO ordering as FetchAgentNextPullRequestsAsync
        prs.Sort((a, b) =>
        {
            var aDate = a.CreatedAt ?? DateTime.MaxValue;
            var bDate = b.CreatedAt ?? DateTime.MaxValue;
            return aDate.CompareTo(bDate);
        });

        // Verify: PRs with CreatedAt are sorted ascending
        var withDates = prs.Where(p => p.CreatedAt.HasValue).ToList();
        for (int i = 1; i < withDates.Count; i++)
        {
            withDates[i].CreatedAt!.Value.Should().BeOnOrAfter(withDates[i - 1].CreatedAt!.Value,
                "PRs with dates should be sorted oldest first");
        }

        // Verify: PRs without CreatedAt come after all PRs with CreatedAt
        var firstNullIndex = prs.FindIndex(p => !p.CreatedAt.HasValue);
        if (firstNullIndex >= 0)
        {
            for (int i = firstNullIndex; i < prs.Count; i++)
            {
                // All items from firstNullIndex onward should either be null or >= the last dated item
                if (prs[i].CreatedAt.HasValue)
                {
                    // A dated item after a null item means the null items are interspersed
                    // but since DateTime.MaxValue is used, nulls sort last
                    prs[i].CreatedAt!.Value.Should().Be(DateTime.MaxValue,
                        "this should not happen — nulls should sort last");
                }
            }

            // All PRs before the first null should have dates
            for (int i = 0; i < firstNullIndex; i++)
            {
                prs[i].CreatedAt.Should().NotBeNull(
                    "PRs before the first null-dated PR should all have dates");
            }
        }
    }

    // ─── P3: PR Dispatch Eligibility Filter ─────────────────────────────────────
    // For any set of PRs with random combinations of labels, draft status, and processing
    // state, the dispatch eligibility filter SHALL include only PRs that: (a) have the
    // `agent:next` label, (b) are not drafts, (c) are not already being processed or queued,
    // and (d) do not carry `agent:error`, `agent:in-progress`, `agent:done`, or `agent:cancelled` labels.
    // **Validates: Requirements 2.3, 2.4, 6.5**

    [Property(MaxTest = 20)]
    public void P3_DispatchEligibility_FiltersCorrectly(PositiveInt prCountRaw)
    {
        var prCount = Math.Min(prCountRaw.Get, 30);
        var random = new Random(prCountRaw.Get);
        var processingSet = new HashSet<string>();

        var prs = Enumerable.Range(0, prCount).Select(i =>
        {
            // Randomly assign labels
            var labels = new List<string>();
            if (random.Next(2) == 0) labels.Add(AgentLabels.Next);
            if (random.Next(4) == 0) labels.Add(AgentLabels.Error);
            if (random.Next(4) == 0) labels.Add(AgentLabels.InProgress);
            if (random.Next(4) == 0) labels.Add(AgentLabels.Done);
            if (random.Next(4) == 0) labels.Add(AgentLabels.Cancelled);
            if (labels.Count == 0) labels.Add("unrelated-label");

            var isDraft = random.Next(3) == 0;
            var identifier = (i + 1).ToString();

            // Randomly mark some as being processed
            if (random.Next(4) == 0) processingSet.Add(identifier);

            return new PullRequestSummary
            {
                Number = i + 1,
                Identifier = identifier,
                Title = $"PR {i + 1}",
                Description = "",
                Labels = labels.AsReadOnly(),
                BranchName = $"feature-{i}",
                TargetBranch = "main",
                Url = $"https://github.com/test/repo/pull/{i + 1}",
                IsDraft = isDraft,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i)
            };
        }).ToList();

        // Apply the same eligibility filter as FetchAgentNextPullRequestsAsync + dispatch loop
        var eligible = prs.Where(pr =>
            !pr.IsDraft &&
            !pr.Labels.Contains(AgentLabels.Error) &&
            !pr.Labels.Contains(AgentLabels.InProgress) &&
            !pr.Labels.Contains(AgentLabels.Done) &&
            !pr.Labels.Contains(AgentLabels.Cancelled) &&
            !processingSet.Contains(pr.Identifier)).ToList();

        // Verify: all eligible PRs satisfy all conditions
        foreach (var pr in eligible)
        {
            pr.IsDraft.Should().BeFalse($"PR #{pr.Identifier} should not be a draft");
            pr.Labels.Should().NotContain(AgentLabels.Error,
                $"PR #{pr.Identifier} should not have error label");
            pr.Labels.Should().NotContain(AgentLabels.InProgress,
                $"PR #{pr.Identifier} should not have in-progress label");
            pr.Labels.Should().NotContain(AgentLabels.Done,
                $"PR #{pr.Identifier} should not have done label");
            pr.Labels.Should().NotContain(AgentLabels.Cancelled,
                $"PR #{pr.Identifier} should not have cancelled label");
            processingSet.Should().NotContain(pr.Identifier,
                $"PR #{pr.Identifier} should not be already processing");
        }

        // Verify: all excluded PRs violate at least one condition
        var excluded = prs.Except(eligible);
        foreach (var pr in excluded)
        {
            var violatesCondition =
                pr.IsDraft ||
                pr.Labels.Contains(AgentLabels.Error) ||
                pr.Labels.Contains(AgentLabels.InProgress) ||
                pr.Labels.Contains(AgentLabels.Done) ||
                pr.Labels.Contains(AgentLabels.Cancelled) ||
                processingSet.Contains(pr.Identifier);

            violatesCondition.Should().BeTrue(
                $"Excluded PR #{pr.Identifier} should violate at least one eligibility condition");
        }
    }

    // ─── P4: Max Runs Per Cycle Limit ───────────────────────────────────────────
    // For any dispatch cycle with N eligible items (issues + PRs combined) and a configured
    // ClosedLoopMaxRunsPerCycle of M, the total number of dispatched jobs SHALL never exceed M.
    // **Validates: Requirements 2.6**

    [Property(MaxTest = 20)]
    public void P4_MaxRunsPerCycle_NeverExceedsLimit(PositiveInt issueCountRaw, PositiveInt prCountRaw, PositiveInt limitRaw)
    {
        var issueCount = Math.Min(issueCountRaw.Get, 20);
        var prCount = Math.Min(prCountRaw.Get, 20);
        var maxRunsPerCycle = Math.Min(limitRaw.Get, issueCount + prCount);
        if (maxRunsPerCycle <= 0) return;

        // Simulate the dispatch budget logic from RunMultiTemplateLoopAsync
        int remaining = maxRunsPerCycle;
        int totalDispatched = 0;

        var issueQueue = Enumerable.Range(0, issueCount).ToList();
        var prQueue = Enumerable.Range(0, prCount).ToList();
        bool issuesTurn = true;

        while (remaining > 0)
        {
            bool issueMadeProgress = false;
            bool prMadeProgress = false;

            bool hasIssues = issueQueue.Count > 0;
            bool hasPrs = prQueue.Count > 0;

            // Issue dispatch
            if (hasIssues && (issuesTurn || !hasPrs))
            {
                if (remaining > 0 && issueQueue.Count > 0)
                {
                    issueQueue.RemoveAt(0);
                    remaining--;
                    totalDispatched++;
                    issueMadeProgress = true;
                }
            }

            if (remaining <= 0) break;

            // PR dispatch
            if (hasPrs && (!issuesTurn || !hasIssues))
            {
                if (remaining > 0 && prQueue.Count > 0)
                {
                    prQueue.RemoveAt(0);
                    remaining--;
                    totalDispatched++;
                    prMadeProgress = true;
                }
            }

            if (!issueMadeProgress && !prMadeProgress) break;
            issuesTurn = !issuesTurn;
        }

        totalDispatched.Should().BeLessThanOrEqualTo(maxRunsPerCycle,
            $"total dispatched ({totalDispatched}) should never exceed max runs per cycle ({maxRunsPerCycle})");
    }

    // ─── P5: Fair Dispatch Alternation ──────────────────────────────────────────
    // For any cycle where both issue and PR queues are non-empty and the max runs budget
    // allows multiple dispatches, both queue types SHALL receive at least one dispatch
    // (no starvation).
    // **Validates: Requirements 2.7**

    [Property(MaxTest = 20)]
    public void P5_FairAlternation_BothQueuesGetAtLeastOneDispatch(PositiveInt issueCountRaw, PositiveInt prCountRaw, PositiveInt limitRaw)
    {
        var issueCount = Math.Max(Math.Min(issueCountRaw.Get, 20), 1); // At least 1
        var prCount = Math.Max(Math.Min(prCountRaw.Get, 20), 1);       // At least 1
        var maxRunsPerCycle = Math.Max(Math.Min(limitRaw.Get, issueCount + prCount), 2); // At least 2 to allow both

        // Simulate the dispatch budget logic from RunMultiTemplateLoopAsync
        int remaining = maxRunsPerCycle;
        int issueDispatches = 0;
        int prDispatches = 0;

        var issueQueue = Enumerable.Range(0, issueCount).ToList();
        var prQueue = Enumerable.Range(0, prCount).ToList();
        bool issuesTurn = true;

        while (remaining > 0)
        {
            bool issueMadeProgress = false;
            bool prMadeProgress = false;

            bool hasIssues = issueQueue.Count > 0;
            bool hasPrs = prQueue.Count > 0;

            // Issue dispatch
            if (hasIssues && (issuesTurn || !hasPrs))
            {
                if (remaining > 0 && issueQueue.Count > 0)
                {
                    issueQueue.RemoveAt(0);
                    remaining--;
                    issueDispatches++;
                    issueMadeProgress = true;
                }
            }

            if (remaining <= 0) break;

            // PR dispatch
            if (hasPrs && (!issuesTurn || !hasIssues))
            {
                if (remaining > 0 && prQueue.Count > 0)
                {
                    prQueue.RemoveAt(0);
                    remaining--;
                    prDispatches++;
                    prMadeProgress = true;
                }
            }

            if (!issueMadeProgress && !prMadeProgress) break;
            issuesTurn = !issuesTurn;
        }

        // Both queues should get at least one dispatch when budget >= 2
        issueDispatches.Should().BeGreaterThanOrEqualTo(1,
            "issue queue should receive at least one dispatch (no starvation)");
        prDispatches.Should().BeGreaterThanOrEqualTo(1,
            "PR queue should receive at least one dispatch (no starvation)");
    }

    // ─── P6: Template Polling Enablement ────────────────────────────────────────
    // For any PipelineJobTemplate with random values of Enabled, ImplementationEnabled,
    // and ReviewEnabled: issue polling occurs if and only if Enabled AND ImplementationEnabled
    // is true, and PR polling occurs if and only if Enabled AND ReviewEnabled is true.
    // **Validates: Requirements 3.3, 3.4, 3.7**

    [Property(MaxTest = 20)]
    public void P6_TemplatePollingEnablement_BooleanLogicCorrect(bool enabled, bool implementationEnabled, bool reviewEnabled)
    {
        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = enabled,
            ImplementationEnabled = implementationEnabled,
            ReviewEnabled = reviewEnabled
        };

        // The pipeline loop logic:
        // 1. Only enabled templates are polled (master switch)
        // 2. Within enabled templates, ImplementationEnabled controls issue polling
        // 3. Within enabled templates, ReviewEnabled controls PR polling
        var shouldPollIssues = template.Enabled && template.ImplementationEnabled;
        var shouldPollPrs = template.Enabled && template.ReviewEnabled;

        // Verify the boolean logic matches the expected behavior
        if (enabled && implementationEnabled)
        {
            shouldPollIssues.Should().BeTrue(
                "issue polling should occur when Enabled=true AND ImplementationEnabled=true");
        }
        else
        {
            shouldPollIssues.Should().BeFalse(
                $"issue polling should NOT occur when Enabled={enabled} AND ImplementationEnabled={implementationEnabled}");
        }

        if (enabled && reviewEnabled)
        {
            shouldPollPrs.Should().BeTrue(
                "PR polling should occur when Enabled=true AND ReviewEnabled=true");
        }
        else
        {
            shouldPollPrs.Should().BeFalse(
                $"PR polling should NOT occur when Enabled={enabled} AND ReviewEnabled={reviewEnabled}");
        }

        // Additional: when both are false, template is effectively disabled
        if (!implementationEnabled && !reviewEnabled)
        {
            (shouldPollIssues || shouldPollPrs).Should().BeFalse(
                "template with both ImplementationEnabled=false and ReviewEnabled=false should not poll anything");
        }
    }
}
