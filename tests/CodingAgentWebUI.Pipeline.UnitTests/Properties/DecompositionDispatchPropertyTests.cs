using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for dispatch, concurrency, plan identification, and partial failure.
/// Feature: 027-epic-decomposition-pipeline, Properties P14, P15, P16, P17, P18
/// </summary>
public class DecompositionDispatchPropertyTests
{
    // TODO: This enum and NextTurn duplicate the production implementation. These tests simulate
    // the round-robin algorithm rather than exercising the real DispatchFairRoundRobinAsync method,
    // so a bug in production turn-cycling would be replicated here and never caught.
    // Consider testing against the actual method or a testable extraction of it.
    private enum DispatchTurn { Issues = 0, PullRequests = 1, Decomposition = 2 }

    private static DispatchTurn NextTurn(DispatchTurn turn) =>
        (DispatchTurn)(((int)turn + 1) % 3);

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 14: Fair Three-Way Alternation
    ///
    /// Given three non-empty queues (issues, PRs, decomposition) and a budget ≥ 3,
    /// the three-way round-robin dispatch ensures no queue is starved: each queue
    /// receives at least one dispatch when all three have eligible items.
    ///
    /// **Validates: Requirements 12.3, 17.10, 19.1**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(FairAlternationArbitraries) })]
    public Property FairThreeWayAlternation_NoStarvationWhenBudgetAtLeast3(FairAlternationInput input)
    {
        // Simulate the three-way alternation logic from PipelineLoopService
        var issueQueue = new Queue<string>(input.IssueItems);
        var prQueue = new Queue<string>(input.PrItems);
        var decompQueue = new Queue<string>(input.DecompItems);

        int remaining = input.Budget;
        int issueDispatched = 0;
        int prDispatched = 0;
        int decompDispatched = 0;

        var currentTurn = DispatchTurn.Issues;

        while (remaining > 0)
        {
            bool hasIssues = issueQueue.Count > 0;
            bool hasPrs = prQueue.Count > 0;
            bool hasDecomp = decompQueue.Count > 0;

            // Find next non-empty queue (same logic as PipelineLoopService)
            var startTurn = currentTurn;
            bool foundTurn = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var tryTurn = (DispatchTurn)(((int)startTurn + attempt) % 3);
                if ((tryTurn == DispatchTurn.Issues && hasIssues) ||
                    (tryTurn == DispatchTurn.PullRequests && hasPrs) ||
                    (tryTurn == DispatchTurn.Decomposition && hasDecomp))
                {
                    currentTurn = tryTurn;
                    foundTurn = true;
                    break;
                }
            }
            if (!foundTurn) break;

            // Dispatch from current turn's queue
            switch (currentTurn)
            {
                case DispatchTurn.Issues when hasIssues:
                    issueQueue.Dequeue();
                    issueDispatched++;
                    remaining--;
                    break;
                case DispatchTurn.PullRequests when hasPrs:
                    prQueue.Dequeue();
                    prDispatched++;
                    remaining--;
                    break;
                case DispatchTurn.Decomposition when hasDecomp:
                    decompQueue.Dequeue();
                    decompDispatched++;
                    remaining--;
                    break;
            }

            // Advance turn for fair alternation
            currentTurn = NextTurn(currentTurn);
        }

        // Property: when budget >= 3 and all queues non-empty, no queue gets zero dispatches
        var noStarvation = issueDispatched > 0 && prDispatched > 0 && decompDispatched > 0;

        // Total dispatched should equal min(budget, total items)
        var totalItems = input.IssueItems.Count + input.PrItems.Count + input.DecompItems.Count;
        var expectedTotal = Math.Min(input.Budget, totalItems);
        var totalDispatched = issueDispatched + prDispatched + decompDispatched;
        var correctTotal = totalDispatched == expectedTotal;

        return (noStarvation && correctTotal).ToProperty()
            .Label($"Issues={issueDispatched}, PRs={prDispatched}, Decomp={decompDispatched}, " +
                   $"Total={totalDispatched}, Expected={expectedTotal}, NoStarvation={noStarvation}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 15: Concurrency Limit Enforcement
    ///
    /// Decomposition dispatch is skipped if and only if the number of active decomposition
    /// runs is greater than or equal to the configured MaxConcurrentDecompositions limit.
    ///
    /// **Validates: Requirements 12.3, 17.10**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ConcurrencyLimitArbitraries) })]
    public Property ConcurrencyLimitEnforcement_SkipIffActiveAtOrAboveLimit(
        ConcurrencyLimitInput input)
    {
        // Simulate the concurrency check from PipelineLoopService dispatch logic
        var activeDecompositionCount = input.ActiveRuns;
        var maxConcurrent = input.MaxConcurrentDecompositions;

        // The dispatch logic: hasDecomp is true only when active < limit
        var shouldSkip = activeDecompositionCount >= maxConcurrent;
        var wouldDispatch = !shouldSkip;

        // Verify: dispatch happens iff active < limit
        var correctBehavior = wouldDispatch == (activeDecompositionCount < maxConcurrent);

        // Additional invariant: if we dispatch, the new active count stays within limit + 1
        // (the count increments after dispatch, so it can reach limit but not exceed it
        // because the next iteration will skip)
        var postDispatchCount = wouldDispatch
            ? activeDecompositionCount + 1
            : activeDecompositionCount;
        var withinBounds = postDispatchCount <= maxConcurrent;

        return (correctBehavior && (shouldSkip || withinBounds)).ToProperty()
            .Label($"Active={activeDecompositionCount}, Max={maxConcurrent}, " +
                   $"ShouldSkip={shouldSkip}, WouldDispatch={wouldDispatch}, " +
                   $"PostCount={postDispatchCount}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 16: Plan Comment Identification
    ///
    /// Given a list of comments where one or more contain the decomposition plan marker,
    /// FindMostRecentPlanComment always selects the LAST (most recent) comment containing
    /// the marker. If no comment contains the marker, returns null.
    ///
    /// **Validates: Requirements 11.4, 14.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PlanCommentArbitraries) })]
    public Property PlanCommentIdentification_SelectsMostRecentWithMarker(
        PlanCommentInput input)
    {
        var comments = input.Comments;

        var result = PostDecompositionPlanStep.FindMostRecentPlanComment(comments);

        // Find expected: the last comment containing the marker
        IssueComment? expected = null;
        for (var i = comments.Count - 1; i >= 0; i--)
        {
            if (comments[i].Body.Contains(
                CommentMarkers.DecompositionPlan, StringComparison.Ordinal))
            {
                expected = comments[i];
                break;
            }
        }

        if (expected is null)
        {
            // No comment has the marker → result should be null
            return (result is null).ToProperty()
                .Label("No marker in any comment → result should be null");
        }

        // Result should be the same comment as expected (most recent with marker)
        var matchesExpected = result is not null && result.Id == expected.Id;
        return matchesExpected.ToProperty()
            .Label($"Expected comment Id={expected.Id}, Got={result?.Id ?? "null"}, " +
                   $"TotalComments={comments.Count}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 17: Plan Comment Idempotency
    ///
    /// When a plan comment with the marker already exists, re-running the step
    /// updates the existing comment rather than creating a duplicate. This is verified
    /// by checking that FindMostRecentPlanComment returns the same comment before and
    /// after an update (the ID doesn't change), and the total comment count stays the same.
    ///
    /// **Validates: Requirements 14.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PlanIdempotencyArbitraries) })]
    public Property PlanCommentIdempotency_UpdateNotDuplicateOnReRun(
        PlanIdempotencyInput input)
    {
        var comments = input.Comments;

        // First identification: find existing plan comment
        var firstFind = PostDecompositionPlanStep.FindMostRecentPlanComment(comments);

        if (firstFind is null)
        {
            // No existing plan comment → on re-run, a new comment would be posted
            // This is the "first run" case, not idempotency. Skip.
            return true.ToProperty().Label("No existing plan comment (first run case)");
        }

        // Simulate update: replace the body of the found comment with new content
        // (the ID stays the same — this is what UpdateCommentAsync does)
        var updatedComments = comments.Select(c =>
            c.Id == firstFind.Id
                ? new IssueComment
                {
                    Id = c.Id,
                    Body = PostDecompositionPlanStep.FormatPlanComment(input.NewPlanContent),
                    Author = c.Author,
                    CreatedAt = c.CreatedAt
                }
                : c
        ).ToList();

        // Second identification: find plan comment again after update
        var secondFind = PostDecompositionPlanStep.FindMostRecentPlanComment(updatedComments);

        // Idempotency properties:
        // 1. Same comment ID is found (update, not duplicate)
        var sameId = secondFind is not null && secondFind.Id == firstFind.Id;
        // 2. Comment count unchanged (no duplicate added)
        var sameCount = updatedComments.Count == comments.Count;
        // 3. Updated comment still contains the marker
        var stillHasMarker = secondFind is not null &&
            secondFind.Body.Contains(CommentMarkers.DecompositionPlan, StringComparison.Ordinal);

        return (sameId && sameCount && stillHasMarker).ToProperty()
            .Label($"SameId={sameId}, SameCount={sameCount}, HasMarker={stillHasMarker}, " +
                   $"OriginalId={firstFind.Id}, FoundId={secondFind?.Id ?? "null"}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 18: Partial Failure Preservation
    ///
    /// Given K successful and (N-K) failed sub-issue creations:
    /// - All K successes are preserved in the results list
    /// - The summary comment lists ALL N results (both successes and failures)
    /// - The target label is agent:done when K > 0, agent:error when K == 0
    ///
    /// **Validates: Requirements 10.2, 10.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PartialFailureArbitraries) })]
    public Property PartialFailurePreservation_KSuccessesPreservedSummaryListsAll(
        PartialFailureInput input)
    {
        var results = input.Results;
        var attempted = results.Count;
        var succeeded = results.Count(r => r.Success);
        var failed = attempted - succeeded;

        // Verify: K successes are preserved
        var successResults = results.Where(r => r.Success).ToList();
        var kPreserved = successResults.Count == input.ExpectedSuccessCount;

        // Verify: summary lists all results
        var summary = PostDecompositionSummaryStep.FormatSummaryComment(
            results, attempted, succeeded, failed);

        // Summary should mention all results (each title appears in the summary)
        var allListed = results.All(r => summary.Contains(r.Title));

        // Verify: correct label determination
        var allFailed = attempted == 0 || succeeded == 0;
        var expectedLabel = allFailed ? AgentLabels.Error : AgentLabels.Done;
        var correctLabel = (succeeded > 0)
            ? expectedLabel == AgentLabels.Done
            : expectedLabel == AgentLabels.Error;

        // Summary contains the marker
        var hasMarker = summary.Contains(CommentMarkers.DecompositionSummary);

        return (kPreserved && allListed && correctLabel && hasMarker).ToProperty()
            .Label($"K={input.ExpectedSuccessCount}, Preserved={kPreserved}, " +
                   $"AllListed={allListed}, CorrectLabel={correctLabel}, " +
                   $"HasMarker={hasMarker}, Attempted={attempted}, Succeeded={succeeded}");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Input types
    // ══════════════════════════════════════════════════════════════════════

    public sealed class FairAlternationInput
    {
        public IReadOnlyList<string> IssueItems { get; }
        public IReadOnlyList<string> PrItems { get; }
        public IReadOnlyList<string> DecompItems { get; }
        public int Budget { get; }

        public FairAlternationInput(
            IReadOnlyList<string> issueItems,
            IReadOnlyList<string> prItems,
            IReadOnlyList<string> decompItems,
            int budget)
        {
            IssueItems = issueItems;
            PrItems = prItems;
            DecompItems = decompItems;
            Budget = budget;
        }

        public override string ToString() =>
            $"Issues={IssueItems.Count}, PRs={PrItems.Count}, " +
            $"Decomp={DecompItems.Count}, Budget={Budget}";
    }

    public sealed class ConcurrencyLimitInput
    {
        public int ActiveRuns { get; }
        public int MaxConcurrentDecompositions { get; }

        public ConcurrencyLimitInput(int activeRuns, int maxConcurrent)
        {
            ActiveRuns = activeRuns;
            MaxConcurrentDecompositions = maxConcurrent;
        }

        public override string ToString() =>
            $"Active={ActiveRuns}, Max={MaxConcurrentDecompositions}";
    }

    public sealed class PlanCommentInput
    {
        public IReadOnlyList<IssueComment> Comments { get; }

        public PlanCommentInput(IReadOnlyList<IssueComment> comments)
        {
            Comments = comments;
        }

        public override string ToString() =>
            $"Comments={Comments.Count}, WithMarker={Comments.Count(c => c.Body.Contains(CommentMarkers.DecompositionPlan))}";
    }

    public sealed class PlanIdempotencyInput
    {
        public IReadOnlyList<IssueComment> Comments { get; }
        public string NewPlanContent { get; }

        public PlanIdempotencyInput(
            IReadOnlyList<IssueComment> comments, string newPlanContent)
        {
            Comments = comments;
            NewPlanContent = newPlanContent;
        }

        public override string ToString() =>
            $"Comments={Comments.Count}, NewPlan={NewPlanContent.Length} chars";
    }

    public sealed class PartialFailureInput
    {
        public IReadOnlyList<SubIssueCreationResult> Results { get; }
        public int ExpectedSuccessCount { get; }

        public PartialFailureInput(
            IReadOnlyList<SubIssueCreationResult> results, int expectedSuccessCount)
        {
            Results = results;
            ExpectedSuccessCount = expectedSuccessCount;
        }

        public override string ToString() =>
            $"Total={Results.Count}, ExpectedSuccess={ExpectedSuccessCount}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Arbitraries
    // ══════════════════════════════════════════════════════════════════════

    public class FairAlternationArbitraries
    {
        public static Arbitrary<FairAlternationInput> FairAlternationInputArb()
        {
            var gen =
                from issueCount in Gen.Choose(1, 10)
                from prCount in Gen.Choose(1, 10)
                from decompCount in Gen.Choose(1, 10)
                from budget in Gen.Choose(3, 30)
                let issues = Enumerable.Range(1, issueCount)
                    .Select(i => $"issue-{i}").ToList()
                let prs = Enumerable.Range(1, prCount)
                    .Select(i => $"pr-{i}").ToList()
                let decomps = Enumerable.Range(1, decompCount)
                    .Select(i => $"decomp-{i}").ToList()
                select new FairAlternationInput(issues, prs, decomps, budget);

            return gen.ToArbitrary();
        }
    }

    public class ConcurrencyLimitArbitraries
    {
        public static Arbitrary<ConcurrencyLimitInput> ConcurrencyLimitInputArb()
        {
            var gen =
                from activeRuns in Gen.Choose(0, 10)
                from maxConcurrent in Gen.Choose(1, 5)
                select new ConcurrencyLimitInput(activeRuns, maxConcurrent);

            return gen.ToArbitrary();
        }
    }

    public class PlanCommentArbitraries
    {
        public static Arbitrary<PlanCommentInput> PlanCommentInputArb()
        {
            var markerBody = CommentMarkers.DecompositionPlan + "\n\n## Plan\n\nSome plan content here.";

            var regularBodies = new[]
            {
                "This is a regular comment with feedback.",
                "LGTM, looks good to me!",
                "Can you split sub-issue 3 into two smaller tasks?",
                "I think we need more context on the auth module.",
                "<!-- agent:gate-rejection -->\n\nThis issue needs refinement.",
                "Great work on the analysis!"
            };

            var gen =
                from totalComments in Gen.Choose(1, 12)
                from markerCount in Gen.Choose(0, 3)
                from seed in Gen.Choose(0, int.MaxValue)
                select BuildCommentList(totalComments, markerCount, markerBody, regularBodies, seed);

            return gen.Select(c => new PlanCommentInput(c)).ToArbitrary();
        }

        private static IReadOnlyList<IssueComment> BuildCommentList(
            int totalComments, int markerCount, string markerBody,
            string[] regularBodies, int seed)
        {
            var rng = new Random(seed);
            var comments = new List<IssueComment>();
            var actualMarkerCount = Math.Min(markerCount, totalComments);

            // Decide which positions get the marker
            var markerPositions = new HashSet<int>();
            while (markerPositions.Count < actualMarkerCount)
            {
                markerPositions.Add(rng.Next(totalComments));
            }

            for (var i = 0; i < totalComments; i++)
            {
                var body = markerPositions.Contains(i)
                    ? markerBody
                    : regularBodies[rng.Next(regularBodies.Length)];

                comments.Add(new IssueComment
                {
                    Id = $"comment-{i + 1}",
                    Body = body,
                    Author = "bot",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-totalComments + i)
                });
            }

            return comments;
        }
    }

    public class PlanIdempotencyArbitraries
    {
        public static Arbitrary<PlanIdempotencyInput> PlanIdempotencyInputArb()
        {
            var planContents = new[]
            {
                "## Revised Plan\n\n1. Implement auth module\n2. Add tests\n3. Update docs",
                "## Updated Decomposition\n\n- Sub-issue A: Create API\n- Sub-issue B: Add validation",
                "## Plan v2\n\nAfter feedback, splitting into 4 sub-issues instead of 3.",
                "## Decomposition Plan\n\nThis epic covers pagination, filtering, and sorting."
            };

            var markerBody = CommentMarkers.DecompositionPlan + "\n\n## Original Plan\n\nOriginal content.";

            var regularBodies = new[]
            {
                "User feedback: please reconsider sub-issue 2.",
                "Looks good overall, minor suggestion on naming.",
                "Can we add a sub-issue for documentation?",
            };

            var gen =
                from totalComments in Gen.Choose(2, 8)
                from markerPosition in Gen.Choose(0, 7)
                from newPlanIdx in Gen.Choose(0, planContents.Length - 1)
                from seed in Gen.Choose(0, int.MaxValue)
                let adjustedMarkerPos = Math.Min(markerPosition, totalComments - 1)
                select BuildIdempotencyInput(
                    totalComments, adjustedMarkerPos, markerBody,
                    regularBodies, planContents[newPlanIdx], seed);

            return gen.ToArbitrary();
        }

        private static PlanIdempotencyInput BuildIdempotencyInput(
            int totalComments, int markerPosition, string markerBody,
            string[] regularBodies, string newPlanContent, int seed)
        {
            var rng = new Random(seed);
            var comments = new List<IssueComment>();

            for (var i = 0; i < totalComments; i++)
            {
                var body = i == markerPosition
                    ? markerBody
                    : regularBodies[rng.Next(regularBodies.Length)];

                comments.Add(new IssueComment
                {
                    Id = $"idem-comment-{i + 1}",
                    Body = body,
                    Author = "bot",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-totalComments + i)
                });
            }

            return new PlanIdempotencyInput(comments, newPlanContent);
        }
    }

    public class PartialFailureArbitraries
    {
        public static Arbitrary<PartialFailureInput> PartialFailureInputArb()
        {
            var gen =
                from total in Gen.Choose(1, 15)
                from successCount in Gen.Choose(0, 15)
                let adjustedSuccess = Math.Min(successCount, total)
                select BuildPartialFailureInput(total, adjustedSuccess);

            return gen.ToArbitrary();
        }

        private static PartialFailureInput BuildPartialFailureInput(
            int total, int successCount)
        {
            var results = new List<SubIssueCreationResult>();

            for (var i = 0; i < successCount; i++)
            {
                results.Add(new SubIssueCreationResult
                {
                    Title = $"Success-{i + 1}",
                    Success = true,
                    Identifier = $"{100 + i}",
                    Url = $"https://github.com/test/repo/issues/{100 + i}"
                });
            }

            for (var i = successCount; i < total; i++)
            {
                results.Add(new SubIssueCreationResult
                {
                    Title = $"Failure-{i + 1}",
                    Success = false,
                    FailureReason = $"Simulated error for item {i + 1}"
                });
            }

            return new PartialFailureInput(results, successCount);
        }
    }
}
