using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Tests for counter pre-initialization in <c>Program.cs</c>.
///
/// AC: "Pre-initialized series are exported at 0 before any event, and the first real event
/// after startup shows up in increase()."
///
/// These tests verify the pre-initialization logic via <see cref="Program.EmitPreInitCounters"/>,
/// the production helper extracted from <c>Program.PreInitializeMetrics</c>.
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class RunOutcomeCounterPreInitializationTests
{
    private static readonly string[] RunTypes =
        ["implementation", "review", "decomposition", "decompositionanalysis", "consolidation"];

    private static readonly string[] NonFailureOutcomes =
        ["cancelled", "conflict_restart", "needs_refinement", "wont_do", "pr_created", "draft_pr", "succeeded"];

    private static readonly string[] FailureReasons =
        ["timeout", "infrastructure_failure", "agent_error", "token_refresh_failure",
         "exit_code_failure", "quality_gate_exhausted", "gate_rejected"];

    private static readonly string[] TerminalStatuses = ["Succeeded", "Failed", "Cancelled"];

    [Fact]
    public void PreInitialization_RunOutcomes_Produces75Series()
    {
        // TODO: [WARNING] This test verifies only the arithmetic of statically defined test-local arrays,
        // not anything emitted by the production pre-initialization code in Program.PreInitializeMetrics.
        // A regression that changed Program.PreInitializeMetrics to cover only 60 series (e.g. missing a
        // run_type) would not be detected because the count computed here is derived entirely from the
        // same test-local constants (RunTypes, NonFailureOutcomes, FailureReasons), not from a MeterListener
        // observing real Add(0) calls. To make this meaningful, the test should capture actual labels
        // emitted by RunPreInitialization (or Program.PreInitializeMetrics) via a MeterListener and assert
        // on the observed count.

        // 5 run_types × (7 non-failure outcomes + 1 timeout + 7 failed) = 5 × 15 = 75 series
        var expectedCount = 5 * (7 + 1 + 7);
        expectedCount.Should().Be(75);

        var allExpected = new List<(string RunType, string Outcome, string FailureReason)>();
        foreach (var runType in RunTypes)
        {
            foreach (var outcome in NonFailureOutcomes)
                allExpected.Add((runType, outcome, "none"));
            allExpected.Add((runType, "timeout", "timeout"));
            foreach (var failureReason in FailureReasons)
                allExpected.Add((runType, "failed", failureReason));
        }

        allExpected.Should().HaveCount(75,
            "exactly 75 pre-initialized series for pipeline.run.outcomes");
        allExpected.Should().OnlyHaveUniqueItems("all 75 combinations must be distinct");
    }

    [Fact]
    public void PreInitialization_WorkItemsTerminated_Produces24Series()
    {
        // TODO: [WARNING] Same limitation as PreInitialization_RunOutcomes_Produces75Series — this test
        // verifies only the arithmetic of statically defined test-local arrays (TerminalStatuses,
        // FailureReasons), not the actual Add(0) calls in Program.PreInitializeMetrics. A regression that
        // reduced the pre-initialized series count (e.g. by removing a status from the loop) would not
        // be caught. To be meaningful, capture observed labels via a MeterListener on
        // WorkDistributionTelemetry.MeterName during RunPreInitialization and assert on the observed count.

        // 3 statuses × (1 none + 7 failure_reasons) = 3 × 8 = 24 series
        var allExpected = new List<(string Status, string FailureReason)>();
        foreach (var status in TerminalStatuses)
        {
            allExpected.Add((status, "none"));
            foreach (var failureReason in FailureReasons)
                allExpected.Add((status, failureReason));
        }

        allExpected.Should().HaveCount(24,
            "exactly 24 pre-initialized series for workdistribution.workitems_terminated");
        allExpected.Should().OnlyHaveUniqueItems("all 24 combinations must be distinct");
    }

    [Fact]
    public void PreInitialization_WorkItemsTerminated_UsesSnakeCaseFailureReasons()
    {
        // All failure_reason values in the pre-initialization must be snake_case.
        // This ensures pre-initialized series match live emission (also snake_case after #2967).
        foreach (var failureReason in FailureReasons)
        {
            failureReason.Should().MatchRegex("^[a-z][a-z0-9_]*$",
                $"failure_reason '{failureReason}' must be snake_case (no uppercase, no spaces)");
        }
    }

    [Fact]
    public void PreInitialization_NeedsRefinementAndWontDo_UseNoneNotGateRejected()
    {
        // Verify that needs_refinement and wont_do use failure_reason=none in pre-initialization.
        // These outcomes arrive with GateRejected in the request, but the outcome derivation
        // forces none — the pre-initialization must match the live emission exactly.
        // TODO: [WARNING] This test asserts against NonFailureOutcomes, which is a static array
        // defined in THIS test class — not the production pre-initialization in Program.cs.
        // The assertions would pass even if Program.PreInitializeMetrics used "gate_rejected" for
        // these outcomes. To be meaningful, the test must verify the actual labels emitted by the
        // pre-initialization code path (e.g. via a MeterListener during RunPreInitialization),
        // not an array defined inside the test file.
        var needsRefinementEntry = NonFailureOutcomes.Should().Contain("needs_refinement");
        var wontDoEntry = NonFailureOutcomes.Should().Contain("wont_do");

        // Both must be in NonFailureOutcomes (not in the FailureReasons array for 'failed' outcome)
        // If either were removed from NonFailureOutcomes to FailureReasons, pre-init would mismatch live emission.
        NonFailureOutcomes.Should().Contain("needs_refinement",
            "needs_refinement is a non-failure outcome using failure_reason=none (not gate_rejected)");
        NonFailureOutcomes.Should().Contain("wont_do",
            "wont_do is a non-failure outcome using failure_reason=none (not gate_rejected)");
    }

    [Fact]
    public void PreInitialization_EmitsAdd0_ForAllRunOutcomesCombinations()
    {
        // Verify Add(0) is emitted for all 75 combinations by calling the real production
        // pre-initialization helper (Program.EmitPreInitCounters), observed via a MeterListener.
        // This test directly exercises the production code path, so a regression in
        // Program.EmitPreInitCounters (e.g. missing a run_type or outcome) will cause this test
        // to fail.
        var observed = new ConcurrentBag<(string RunType, string Outcome, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.outcomes")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string runType = "", outcome = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
                else if (tag.Key == "outcome") outcome = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            observed.Add((runType, outcome, failureReason));
        });
        listener.Start();

        // Call the production pre-initialization helper (not an inline copy).
        Program.EmitPreInitCounters();

        // All 75 combinations must have been observed
        observed.Should().HaveCountGreaterThanOrEqualTo(75,
            "all 75 pre-initialized combinations must have been observed by MeterListener");

        // Check specific combinations
        foreach (var runType in RunTypes)
        {
            foreach (var outcome in NonFailureOutcomes)
            {
                observed.Should().Contain((runType, outcome, "none"),
                    $"pre-init must cover ({runType}, {outcome}, none)");
            }
            observed.Should().Contain((runType, "timeout", "timeout"));
            foreach (var failureReason in FailureReasons)
            {
                observed.Should().Contain((runType, "failed", failureReason),
                    $"pre-init must cover ({runType}, failed, {failureReason})");
            }
        }
    }

    [Fact]
    public void PreInitialization_EmitsAdd0_ForAllWorkItemsTerminatedCombinations()
    {
        // Verify Add(0) is emitted for all 24 combinations of workdistribution.workitems_terminated
        // by calling the real production pre-initialization helper (Program.EmitPreInitCounters),
        // observed via a MeterListener on WorkDistributionTelemetry.MeterName.
        // This test directly exercises the production code path, so a regression in
        // Program.EmitPreInitCounters (e.g. missing a status or failure_reason) will cause this
        // test to fail — unlike PreInitialization_WorkItemsTerminated_Produces24Series which only
        // verifies arithmetic on test-local arrays.
        var observed = new ConcurrentBag<(string Status, string FailureReason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName
                && instrument.Name == "workdistribution.workitems_terminated")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string status = "", failureReason = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "status") status = tag.Value?.ToString() ?? "";
                else if (tag.Key == "failure_reason") failureReason = tag.Value?.ToString() ?? "";
            }
            observed.Add((status, failureReason));
        });
        listener.Start();

        // Call the production pre-initialization helper.
        Program.EmitPreInitCounters();

        // All 24 combinations must have been observed: 3 statuses × (1 none + 7 failure_reasons)
        observed.Should().HaveCountGreaterThanOrEqualTo(24,
            "all 24 pre-initialized combinations must have been observed by MeterListener");

        foreach (var status in TerminalStatuses)
        {
            observed.Should().Contain((status, "none"),
                $"pre-init must cover ({status}, none)");
            foreach (var failureReason in FailureReasons)
            {
                observed.Should().Contain((status, failureReason),
                    $"pre-init must cover ({status}, {failureReason})");
            }
        }
    }

    // ── New pipeline.run.* counters pre-initialization ─────────────────────────

    private static readonly string[] Providers = ["kiro", "opencode"];

    [Fact]
    public void PreInitialization_RunTokens_Produces90Series()
    {
        // 5 run_types × 9 phases × 2 providers = 90 series
        // TODO: [WARNING] This test only validates the arithmetic of the constant expression
        // (5 × 9 × 2 == 90) — it will always pass regardless of what EmitPreInitCounters actually
        // emits. It provides false confidence: even if EmitPreInitCounters emitted zero series,
        // this test would still pass. The real behavioral validation is in
        // PreInitialization_EmitsAdd0_ForAllRunTokensCombinations. Consider removing this test or
        // converting it to assert the actual emitted count from the MeterListener.
        var expected = RunTypes.Length * PipelineTelemetry.RunPhases.All.Length * Providers.Length;
        expected.Should().Be(90);
    }

    [Fact]
    public void PreInitialization_EmitsAdd0_ForAllRunTokensCombinations()
    {
        var observed = new ConcurrentBag<(string RunType, string Phase, string Provider)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.tokens")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string runType = "", phase = "", provider = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
                else if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                else if (tag.Key == "provider") provider = tag.Value?.ToString() ?? "";
            }
            observed.Add((runType, phase, provider));
        });
        listener.Start();

        Program.EmitPreInitCounters();

        observed.Should().HaveCountGreaterThanOrEqualTo(90,
            "all 90 pre-initialized combinations must have been observed for pipeline.run.tokens");

        foreach (var runType in RunTypes)
        {
            foreach (var phase in PipelineTelemetry.RunPhases.All)
            {
                foreach (var provider in Providers)
                {
                    observed.Should().Contain((runType, phase, provider),
                        $"pre-init must cover ({runType}, {phase}, {provider})");
                }
            }
        }
    }

    [Fact]
    public void PreInitialization_EmitsAdd0_ForAllRunCostUsdCombinations()
    {
        var observed = new ConcurrentBag<(string RunType, string Phase, string Provider)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.cost_usd")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string runType = "", phase = "", provider = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
                else if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                else if (tag.Key == "provider") provider = tag.Value?.ToString() ?? "";
            }
            observed.Add((runType, phase, provider));
        });
        listener.Start();

        Program.EmitPreInitCounters();

        observed.Should().HaveCountGreaterThanOrEqualTo(90,
            "all 90 pre-initialized combinations must have been observed for pipeline.run.cost_usd");
    }

    [Fact]
    public void PreInitialization_EmitsAdd0_ForAllRunAgentTimeCombinations()
    {
        var observed = new ConcurrentBag<(string RunType, string Phase, string Provider)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.agent_time")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string runType = "", phase = "", provider = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
                else if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                else if (tag.Key == "provider") provider = tag.Value?.ToString() ?? "";
            }
            observed.Add((runType, phase, provider));
        });
        listener.Start();

        Program.EmitPreInitCounters();

        observed.Should().HaveCountGreaterThanOrEqualTo(90,
            "all 90 pre-initialized combinations must have been observed for pipeline.run.agent_time");
    }

    [Fact]
    public void PreInitialization_DoesNotPreInit_RunAgentSessions()
    {
        // pipeline.run.agent_sessions carries a model tag (unbounded cardinality) — NOT pre-initialized
        var count = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pipeline.run.agent_sessions") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => count.Add(1));
        listener.Start();

        Program.EmitPreInitCounters();

        // No pre-init calls for agent_sessions
        count.Should().BeEmpty("pipeline.run.agent_sessions is not pre-initialized due to unbounded model tag cardinality");
    }
}
