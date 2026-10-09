namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Background-loop placement guardrails (ARCH-T1-002, narrowed by verification). decisions.md
/// "Dispatch loop belongs in a leader-elected controller": every background loop runs on exactly one
/// elected leader — dispatch in the Scheduler, reconciliation in the JobController.
/// The T4 class-scanner fix and its ReconciliationService positive control live next to T4 in LayerBoundaryTests.cs.
/// </summary>
public partial class LayerBoundaryTests
{
    private static readonly string SchedulerRegistrationFile =
        Path.Combine(RepoRoot, "src", "CodingAgent.Scheduler", "SchedulerServiceCollectionExtensions.cs");

    // ── Scheduler: the leader gate is always registered by the production composition root ──
    // The Scheduler loops resolve their gate with sp.GetService<ILeaderElectionService>() and run UNGATED when
    // it is null. That fallback is documented intent for test/dev hosts (PipelineLoopServiceDependencies.LeaderElection,
    // LoopWatchdogService; SchedulerE2EWebApplicationFactory removes the registration on purpose), so the sites
    // stay GetService. What must not happen is AddSchedulerServices — the method the Scheduler's Program.cs calls —
    // losing the registration or making it conditional: every replica would then run every loop ("seen live:
    // 4 pods for a cap of 3"). This pins the registration at the method-body level of AddSchedulerServices.
    //
    // Source scan, not a DI test: this test project does not reference CodingAgent.Scheduler (its DLL is not in
    // the output), so it cannot call AddSchedulerServices. The scan is anchored on the registration line and on
    // the method's own brace lines; if the registration moves into a helper method, update the anchors.
    [Fact]
    public void Scheduler_AddSchedulerServices_RegistersLeaderElectionUnconditionally()
    {
        // The scanned method is the Scheduler host's real composition root: Program.cs calls it as a top-level statement.
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src", "CodingAgent.Scheduler", "Program.cs"));
        Assert.Matches(SchedulerProgramCallRegex(), program);

        var scan = ScanSchedulerLeaderElectionRegistration(File.ReadAllText(SchedulerRegistrationFile));
        Assert.True(scan.MethodFound, $"AddSchedulerServices not found in {SchedulerRegistrationFile}.");

        // Positive control — the scanned body contains the nullable-gate consumers this registration protects.
        Assert.True(scan.NullableGateConsumers > 0,
            "No GetService<ILeaderElectionService>() consumer found in AddSchedulerServices — the body scan is not " +
            "seeing the loop registrations (or the loops now use GetRequiredService; then retire this test).");

        Assert.True(scan.UnconditionalRegistrations > 0,
            $"AddSchedulerServices must register ILeaderElectionService unconditionally (found " +
            $"{scan.ConditionalRegistrations} conditional registration(s)). The Scheduler loops resolve the gate with " +
            $"GetService<ILeaderElectionService>() ({scan.NullableGateConsumers} sites) and run ungated on every " +
            "replica when it is missing. The null fallback is for test hosts only (decisions.md: every background " +
            "loop runs on exactly one elected leader).");
    }

    // Proves the scan above fails on what it protects against: the real file with the registration removed,
    // wrapped in an if block, or placed under a braceless if.
    [Fact]
    public void SchedulerLeaderElectionScan_DetectsRemovedOrConditionalRegistration()
    {
        var source = File.ReadAllText(SchedulerRegistrationFile);
        var line = source.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => LeaderElectionRegistrationRegex().IsMatch(l));
        Assert.NotNull(line);
        var pad = line[..IndentOf(line)];
        const string condition = "if (config.GetValue(\"LeaderElection:Enabled\", defaultValue: true))";

        // TODO: string.Replace replaces ALL occurrences of `line`. If the registration line appears
        // verbatim more than once in the file (e.g., a duplicate registration or matching comment),
        // the `removed` mutation zeros out more than intended. Use a single-occurrence replace if
        // the file gains a second matching line to preserve the single-removal semantics.
        var removed = ScanSchedulerLeaderElectionRegistration(source.Replace(line, string.Empty, StringComparison.Ordinal));
        var wrapped = ScanSchedulerLeaderElectionRegistration(source.Replace(line,
            $"{pad}{condition}\n{pad}{{\n    {line}\n{pad}}}", StringComparison.Ordinal));
        var braceless = ScanSchedulerLeaderElectionRegistration(source.Replace(line,
            $"{pad}{condition}\n    {line}", StringComparison.Ordinal));

        Assert.Equal((true, 0, 0), (removed.MethodFound, removed.UnconditionalRegistrations, removed.ConditionalRegistrations));
        Assert.Equal((true, 0, 1), (wrapped.MethodFound, wrapped.UnconditionalRegistrations, wrapped.ConditionalRegistrations));
        Assert.Equal((true, 0, 1), (braceless.MethodFound, braceless.UnconditionalRegistrations, braceless.ConditionalRegistrations));
    }

    private sealed record LeaderRegistrationScan(
        bool MethodFound, int UnconditionalRegistrations, int ConditionalRegistrations, int NullableGateConsumers);

    // Scans the body of AddSchedulerServices — delimited by the "{" and "}" lines at the signature's own
    // indentation — for `services.AddSingleton<ILeaderElectionService>(` lines. A registration indented at the
    // body's statement level is unconditional; one indented deeper sits inside an if/else block, a lambda, or
    // under a braceless conditional. Also counts the GetService<ILeaderElectionService>() consumers in the body.
    private static LeaderRegistrationScan ScanSchedulerLeaderElectionRegistration(string source)
    {
        var lines = source.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var signature = Array.FindIndex(lines, l => AddSchedulerServicesSignatureRegex().IsMatch(l));
        if (signature < 0) return new(false, 0, 0, 0);

        var indent = IndentOf(lines[signature]);
        var open = Array.FindIndex(lines, signature, l => l.Trim() == "{" && IndentOf(l) == indent);
        var close = open < 0 ? -1 : Array.FindIndex(lines, open + 1, l => l.Trim() == "}" && IndentOf(l) == indent);
        if (close < 0) return new(false, 0, 0, 0);

        var body = lines[(open + 1)..close];
        // TODO: statementIndent is derived from the first non-blank line in the method body. If that
        // line is a block comment, a `#region` directive, or an attribute at a different indentation,
        // the baseline will be wrong and the unconditional/conditional classification of all
        // registration lines will be incorrect. Update this to skip comment/directive lines if
        // AddSchedulerServices gains a leading comment or attribute inside its body.
        var statementIndent = body.Where(l => l.Trim().Length > 0).Select(IndentOf).DefaultIfEmpty(0).First();
        var registrations = body.Where(l => LeaderElectionRegistrationRegex().IsMatch(l)).ToList();
        var unconditional = registrations.Count(l => IndentOf(l) == statementIndent);
        var consumers = body.Count(l => l.Contains("GetService<ILeaderElectionService>()", StringComparison.Ordinal));
        return new(true, unconditional, registrations.Count - unconditional, consumers);
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart(' ', '\t').Length;

    // ── ReconciliationService runs only in the JobController host ──
    // decisions.md: "reconciliation in the JobController". T4 (now that ClassNameRegex sees non-sealed classes)
    // fails if no host registers ReconciliationService; this pins that no OTHER host can: the type lives in the
    // JobController host assembly, and no src project references that host. Assembly and csproj facts are used
    // instead of a source parser for AddHostedService call sites.
    [Fact]
    public void ReconciliationService_CanOnlyBeRegisteredByJobControllerHost()
    {
        Assert.True(
            typeof(CodingAgent.JobController.Reconciliation.ReconciliationService).Assembly.GetName().Name
                == "CodingAgent.JobController",
            "ReconciliationService moved out of the JobController host assembly, so other hosts could now register " +
            "it. Reconciliation must run only in the JobController (decisions.md); pin its new placement before moving it.");

        var graph = SrcProjectReferenceGraph();
        var referencers = graph.Keys
            .Where(p => p != "CodingAgent.JobController")
            .Where(p => ProjectReferenceClosure(graph, p).Contains("CodingAgent.JobController"))
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(referencers.Count == 0,
            $"These src projects reference the JobController host and could register ReconciliationService, which " +
            $"must run only in the JobController (decisions.md): {string.Join(", ", referencers)}.");

        // Positive control — the csproj parser sees a ProjectReference to the JobController where one exists
        // (this test project's own).
        var testProject = Path.Combine(RepoRoot, "tests", "CodingAgent.Pipeline.UnitTests", "CodingAgent.Pipeline.UnitTests.csproj");
        Assert.Contains("CodingAgent.JobController", ProjectReferencesOf(testProject));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bpublic\s+static\s+IServiceCollection\s+AddSchedulerServices\s*\(")]
    private static partial System.Text.RegularExpressions.Regex AddSchedulerServicesSignatureRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*services\.AddSingleton<ILeaderElectionService>\(")]
    private static partial System.Text.RegularExpressions.Regex LeaderElectionRegistrationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^builder\.Services\.AddSchedulerServices\(",
        System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex SchedulerProgramCallRegex();
}
