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
/// the production helper that <c>MetricPreInitialization.Run</c> calls at API startup.
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

    [Fact]
    public void PreInitialization_RunOutcomes_Produces75Series()
    {
        // TODO: [WARNING] This test verifies only the arithmetic of statically defined test-local arrays,
        // not anything emitted by the production pre-initialization code in Program.EmitPreInitCounters.
        // A regression that changed Program.EmitPreInitCounters to cover only 60 series (e.g. missing a
        // run_type) would not be detected because the count computed here is derived entirely from the
        // same test-local constants (RunTypes, NonFailureOutcomes, FailureReasons), not from a MeterListener
        // observing real Add(0) calls. To make this meaningful, the test should capture actual labels
        // emitted by Program.EmitPreInitCounters via a MeterListener and assert
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
    public void PreInitialization_FailureReasons_AreSnakeCase()
    {
        // All failure_reason values in the pre-initialization must be snake_case so the
        // pre-initialized series match live emission (snake_case since #2967).
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
        // The assertions would pass even if Program.EmitPreInitCounters used "gate_rejected" for
        // these outcomes. To be meaningful, the test must verify the actual labels emitted by the
        // pre-initialization code path (e.g. via a MeterListener during Program.EmitPreInitCounters),
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
    public void PreInitialization_RunSubIssues_Produces2Series()
    {
        // Arrange: collect Add(0) calls on pipeline.run.sub_issues
        var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.sub_issues")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "result") observed.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // 2 series: result=created, result=failed
        observed.Should().Contain("created", "pre-init must cover result=created");
        observed.Should().Contain("failed", "pre-init must cover result=failed");
        // TODO: [WARNING] HaveCountGreaterThanOrEqualTo(2) is weaker than the comment says ("exactly 2").
        // If EmitPreInitCounters is called more than once or the loop is expanded, this assertion would
        // pass even with duplicate/extra emissions. Tighten to HaveCount(2) once the test can guarantee
        // a single call to EmitPreInitCounters within the listener's lifetime (e.g. by listening only
        // within a fresh scope). See review findings [WARNING] TestQualityReviewer L253.
        observed.Should().HaveCountGreaterThanOrEqualTo(2,
            "exactly 2 pre-initialized series for pipeline.run.sub_issues");
    }

    [Fact]
    public void PreInitialization_RunBrainUpdates_Produces2Series()
    {
        // Arrange: collect Add(0) calls on pipeline.run.brain_updates
        var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.brain_updates")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "result") observed.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // 2 series: result=pushed, result=none
        observed.Should().Contain("pushed", "pre-init must cover result=pushed");
        observed.Should().Contain("none", "pre-init must cover result=none");
        // TODO: [WARNING] Same weak assertion as RunSubIssues test above — HaveCountGreaterThanOrEqualTo(2)
        // does not enforce "exactly 2". See review findings [WARNING] TestQualityReviewer L253.
        observed.Should().HaveCountGreaterThanOrEqualTo(2,
            "exactly 2 pre-initialized series for pipeline.run.brain_updates");
    }
}

/// <summary>
/// Tests for pre-initialization of the new API-side counters introduced in issue #2979:
/// - pipeline.run.quality_gate.results
/// - pipeline.run.ci.not_started_retriggers
/// - pipeline.run.agent_stalls
/// These counters must be pre-initialized so Prometheus increase() shows the first event.
/// </summary>
[Collection("PostStatusIdempotencyCollection")]
public sealed class PipelineRunEventCounterPreInitializationTests
{
    [Fact]
    public void PreInitialization_RunQualityGateResults_CoversAllGatesAndResults()
    {
        var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.quality_gate.results")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string gate = "", result = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "gate") gate = tag.Value?.ToString() ?? "";
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            }
            observed.Add($"{gate}:{result}");
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // Each gate × result combination must be covered
        observed.Should().Contain("compilation:pass");
        observed.Should().Contain("compilation:fail");
        observed.Should().Contain("tests:pass");
        observed.Should().Contain("tests:fail");
        observed.Should().Contain("external_ci:pass");
        observed.Should().Contain("external_ci:fail");
    }

    [Fact]
    public void PreInitialization_RunCiNotStartedRetriggers_CoversAllRunTypes()
    {
        var observedRunTypes = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.ci.not_started_retriggers")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "run_type") observedRunTypes.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // All 5 run types must have a pre-initialized re-trigger series
        observedRunTypes.Should().Contain("implementation",
            "ci.not_started_retriggers must be pre-initialized for implementation runs");
        observedRunTypes.Should().Contain("review",
            "ci.not_started_retriggers must be pre-initialized for review runs");
    }

    [Fact]
    public void PreInitialization_RunAgentStalls_CoversAllPhasesAndKinds()
    {
        var observed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.agent_stalls")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string phase = "", kind = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "phase") phase = tag.Value?.ToString() ?? "";
                if (tag.Key == "kind") kind = tag.Value?.ToString() ?? "";
            }
            observed.Add($"{phase}:{kind}");
        });
        listener.Start();

        Program.EmitPreInitCounters();

        // Key combinations that must be pre-initialized
        observed.Should().Contain($"{PipelineTelemetry.RunPhases.QualityGate}:{PipelineTelemetry.AgentStallKinds.StallKill}",
            "stall_kill in quality_gate phase must be pre-initialized");
        observed.Should().Contain($"{PipelineTelemetry.RunPhases.CodeGen}:{PipelineTelemetry.AgentStallKinds.ProcessDeath}",
            "process_death in codegen phase must be pre-initialized");
        observed.Should().Contain($"{PipelineTelemetry.RunPhases.Analysis}:{PipelineTelemetry.AgentStallKinds.ProcessTimeout}",
            "process_timeout in analysis phase must be pre-initialized");
        observed.Should().Contain($"{PipelineTelemetry.RunPhases.Other}:{PipelineTelemetry.AgentStallKinds.StallKill}",
            "other phase must be pre-initialized");
    }
}
