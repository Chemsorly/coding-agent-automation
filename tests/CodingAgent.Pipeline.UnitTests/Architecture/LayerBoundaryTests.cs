using NetArchTest.Rules;

namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Enforces layer dependency rules to prevent architectural erosion.
/// These tests encode the project dependency direction rules from the architecture analysis:
/// - Pipeline must NOT reference Infrastructure or Orchestration
/// - Infrastructure.Providers must NOT reference Orchestration or WebUI
/// - Infrastructure.Persistence must NOT reference Orchestration or WebUI
/// - Infrastructure.Persistence references Providers (one-way)
/// - Agent projects must NOT reference Orchestration
/// - Agent must NOT reference Infrastructure.Persistence (T9 invariant — compile-time enforcement)
/// </summary>
public partial class LayerBoundaryTests
{
    // Assembly anchors for each layer
    private static readonly System.Reflection.Assembly PipelineAssembly =
        typeof(Pipeline.Services.PipelineOrchestrationService).Assembly;

    // Spec 048 Phase 1: Contracts is the extracted shared surface. Its types keep the
    // CodingAgent.Pipeline.* namespaces (namespace-preserving move), so the boundary
    // is checked at the ASSEMBLY-reference level, not by namespace.
    private static readonly System.Reflection.Assembly ContractsAssembly =
        typeof(CodingAgent.Pipeline.Models.PipelineRunSummary).Assembly;

    // Spec 048 Phase 1 (cont.): Infrastructure.Common holds shared LeaderElection + Telemetry +
    // SecretMasker + Serilog-OTLP + GitHub JWT/issue-ref utils. References Contracts, never Pipeline.
    private static readonly System.Reflection.Assembly InfrastructureCommonAssembly =
        typeof(CodingAgent.Pipeline.Telemetry.PipelineTelemetry).Assembly;

    // T9 split: Infrastructure is now two assemblies.
    // Providers: no EF Core, no Npgsql — safe for untrusted agent pods.
    // Persistence: EF Core + Npgsql — API and orchestrator only.
    private static readonly System.Reflection.Assembly InfrastructureProvidersAssembly =
        typeof(CodingAgent.Infrastructure.GitHub.GitHubRepositoryProvider).Assembly;

    private static readonly System.Reflection.Assembly InfrastructurePersistenceAssembly =
        typeof(CodingAgent.Infrastructure.Persistence.PipelineDbContext).Assembly;

    private static readonly System.Reflection.Assembly AgentAssembly =
        typeof(CodingAgent.Agent.WorkItemAgentService).Assembly;

    private static readonly System.Reflection.Assembly AgentKiroCliAssembly =
        typeof(CodingAgent.Agent.KiroCli.KiroCliAgentProvider).Assembly;

    private static readonly System.Reflection.Assembly AgentOpenCodeAssembly =
        typeof(CodingAgent.Agent.OpenCode.OpenCodeAgentProvider).Assembly;

    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(CodingAgent.Api.ApiHostMarker).Assembly;

    private static readonly System.Reflection.Assembly OrchestrationAssembly =
        typeof(CodingAgent.Orchestration.RunLifecycleManager).Assembly;

    private static readonly System.Reflection.Assembly HubAssembly =
        typeof(CodingAgent.AgentGateway.AgentHubFacade).Assembly;

    // Repo root: walk up from the test binary directory until we find CodingAgentAutomation.sln
    private static readonly string RepoRoot = FindRepoRoot(AppContext.BaseDirectory);

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.GetFiles("CodingAgentAutomation.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not find repo root from '{start}'");
    }

    // ── Spec 048 Phase 1: Contracts boundary ────────────────────────────
    // Contracts must never reference the Pipeline assembly — the whole point of the
    // extraction. Checked via assembly references because the two share namespaces.
    // Positive control: Pipeline DOES reference Contracts, proving both the mechanism
    // and the one-way direction (Pipeline → Contracts, never the reverse).
    [Fact]
    public void Contracts_ShouldNot_ReferencePipelineAssembly()
    {
        var contractsRefs = ContractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name).ToList();
        Assert.DoesNotContain("CodingAgent.Pipeline", contractsRefs);

        // Positive control — if this fails, the reflection check is not seeing references.
        var pipelineRefs = PipelineAssembly.GetReferencedAssemblies()
            .Select(a => a.Name).ToList();
        Assert.Contains("CodingAgent.Contracts", pipelineRefs);
    }

    // Infrastructure.Common is below Pipeline in the graph — Pipeline references it, never the reverse.
    [Fact]
    public void InfrastructureCommon_ShouldNot_ReferencePipelineAssembly()
    {
        var refs = InfrastructureCommonAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("CodingAgent.Pipeline", refs);
    }

    // ── Spec 048 Phase 2: Database isolation boundary ───────────────────
    // Only the API host may reference Infrastructure.Persistence. Hub and Orchestration reach the
    // database exclusively through Contracts interfaces (IWorkItemTransitionStore,
    // IWorkItemFallbackTransitionService, the config/history/store adapters). Checked at the
    // ASSEMBLY-reference level, not by namespace: the moved interfaces keep their
    // CodingAgent.Infrastructure.Persistence.Services namespace (namespace-preserving move) but
    // now live in the Contracts assembly, so a namespace-based HaveDependencyOn would false-positive.
    [Fact]
    public void Hub_ShouldNot_ReferenceInfrastructurePersistenceAssembly()
    {
        var refs = HubAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("CodingAgent.Infrastructure.Persistence", refs);
    }

    [Fact]
    public void Orchestration_ShouldNot_ReferenceInfrastructurePersistenceAssembly()
    {
        var refs = OrchestrationAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("CodingAgent.Infrastructure.Persistence", refs);
    }

    // Positive control — the API IS the sole database owner, so it MUST reference Persistence.
    // Without this, the two negative tests above could pass vacuously (a "clean" boundary that is
    // clean only because nothing references Persistence anywhere).
    [Fact]
    public void Api_DoesReference_InfrastructurePersistenceAssembly()
    {
        var refs = ApiAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.Contains("CodingAgent.Infrastructure.Persistence", refs);
    }

    // Assembly-level counterpart to the source-scan Monolith_ShouldNot_OwnDatabase below: walk the
    // Web host's transitive assembly closure and assert Infrastructure.Persistence never appears.
    // This locks in the Phase-2 outcome — the transitive EF pull (Web → Hub/Orchestration →
    // Persistence) is removed and must stay removed, even if a future source-level EF reference is
    // added indirectly. Mirrors JobController_Closure_IsPipelineFree.
    [Fact]
    public void WebHost_Closure_IsPersistenceFree()
    {
        // Resolve from the test's own output directory — every referenced project DLL is copied here,
        // so this works under any build configuration. (A hardcoded bin/Debug path fails in CI, which
        // builds --configuration Release.) The walk only follows CodingAgent.Web.dll's own transitive
        // references, so unrelated assemblies also present in this flat dir do not affect the result.
        var start = Path.Combine(AppContext.BaseDirectory, "CodingAgent.Web.dll");
        Assert.True(File.Exists(start), $"CodingAgent.Web.dll not found at {start} — build first.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var offenders = new List<string>();
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            System.Reflection.Assembly asm;
            try { asm = System.Reflection.Assembly.LoadFrom(path); }
            catch { continue; }
            foreach (var r in asm.GetReferencedAssemblies())
            {
                if (r.Name is null || !r.Name.StartsWith("CodingAgent", StringComparison.Ordinal)) continue;
                if (r.Name == "CodingAgent.Infrastructure.Persistence")
                    offenders.Add($"{asm.GetName().Name} -> {r.Name}");
                if (seen.Add(r.Name))
                {
                    var dep = Path.Combine(Path.GetDirectoryName(path)!, r.Name + ".dll");
                    if (File.Exists(dep)) queue.Enqueue(dep);
                }
            }
        }
        Assert.True(offenders.Count == 0,
            $"Web host closure references Infrastructure.Persistence (should be Persistence-free — " +
            $"Spec 048 Phase 2): {string.Join(", ", offenders)}");
    }

    // Spec 048 COMMIT 3 goal: JobController's shipped image must not contain the Pipeline execution
    // engine. Walk the transitive assembly-reference closure of the built JobController.dll and assert
    // CodingAgent.Pipeline never appears anywhere in it.
    [Fact]
    public void JobController_Closure_IsPipelineFree()
    {
        // Resolve from the test's own output directory (config-agnostic; CI builds Release, so a
        // hardcoded bin/Debug path would not exist). The walk follows only JobController.dll's own
        // transitive references, so other assemblies present in this flat dir do not affect the result.
        var start = Path.Combine(AppContext.BaseDirectory, "CodingAgent.JobController.dll");
        Assert.True(File.Exists(start), $"JobController.dll not found at {start} — build first.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var offenders = new List<string>();
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            System.Reflection.Assembly asm;
            try { asm = System.Reflection.Assembly.LoadFrom(path); }
            catch { continue; }
            foreach (var r in asm.GetReferencedAssemblies())
            {
                if (r.Name is null || !r.Name.StartsWith("CodingAgent", StringComparison.Ordinal)) continue;
                if (r.Name == "CodingAgent.Pipeline")
                    offenders.Add($"{asm.GetName().Name} -> {r.Name}");
                if (seen.Add(r.Name))
                {
                    var dep = Path.Combine(Path.GetDirectoryName(path)!, r.Name + ".dll");
                    if (File.Exists(dep)) queue.Enqueue(dep);
                }
            }
        }
        Assert.True(offenders.Count == 0,
            $"JobController closure references Pipeline (should be Pipeline-free): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Pipeline_ShouldNot_DependOnInfrastructure()
    {
        var result = Types.InAssembly(PipelineAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pipeline layer must not reference Infrastructure. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void Pipeline_ShouldNot_DependOnOrchestration()
    {
        var result = Types.InAssembly(PipelineAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pipeline layer must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void Pipeline_ShouldNot_DependOnWebUI()
    {
        var result = Types.InAssembly(PipelineAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "CodingAgent.AgentGateway",
                "CodingAgent.Web.Services",
                "CodingAgent.Web.Components",
                "CodingAgent.Web.Models")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pipeline must not reference WebUI namespaces. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void Infrastructure_ShouldNot_DependOnOrchestration()
    {
        // Check both Providers and Persistence assemblies — neither should reach Orchestration.
        foreach (var (assembly, name) in new[]
        {
            (InfrastructureProvidersAssembly, "Infrastructure.Providers"),
            (InfrastructurePersistenceAssembly, "Infrastructure.Persistence"),
        })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn("CodingAgent.Orchestration")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{name} must not reference Orchestration. Violating types: {FormatViolations(result)}");
        }
    }

    [Fact]
    public void Infrastructure_ShouldNot_DependOnWebUI()
    {
        // Check both Providers and Persistence assemblies — neither should reach WebUI.
        foreach (var (assembly, name) in new[]
        {
            (InfrastructureProvidersAssembly, "Infrastructure.Providers"),
            (InfrastructurePersistenceAssembly, "Infrastructure.Persistence"),
        })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "CodingAgent.AgentGateway",
                    "CodingAgent.Web.Services",
                    "CodingAgent.Web.Components",
                    "CodingAgent.Web.Models")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{name} must not reference WebUI namespaces. Violating types: {FormatViolations(result)}");
        }
    }

    [Fact]
    public void AgentKiroCli_ShouldNot_DependOnOrchestration()
    {
        var result = Types.InAssembly(AgentKiroCliAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Agent.KiroCli must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void AgentOpenCode_ShouldNot_DependOnOrchestration()
    {
        var result = Types.InAssembly(AgentOpenCodeAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Agent.OpenCode must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void AgentKiroCli_ShouldNot_DependOnOrchestration_ViaAgent()
    {
        // The main Agent assembly carries GitHub/GitLab/Resilience from Infrastructure
        // because the agent directly instantiates repository providers and uses resilience
        // helpers. This is a known architectural violation (tracked in architecture analysis).
        // This test documents the *current* boundary that MUST NOT get worse:
        // Agent must not reach into Orchestration (the in-process dispatch layer).
        var result = Types.InAssembly(AgentAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Agent must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    // ── Positive Control ────────────────────────────────────────────────
    // Proves NetArchTest is actually detecting dependencies. If this test fails,
    // the entire test class is unreliable (framework not scanning correctly).

    [Fact]
    public void PositiveControl_Pipeline_DependsOnCodeReview()
    {
        // Pipeline is known to reference Pipeline.CodeReview via ProjectReference.
        // If NetArchTest can't detect this known dependency, all negative tests are unreliable.
        var result = Types.InAssembly(PipelineAssembly)
            .That()
            .HaveDependencyOn("CodingAgent.Pipeline.CodeReview")
            .GetTypes();

        Assert.NotEmpty(result);
    }

    // ── Dispatch duplication guard ──────────────────────────────────────
    // Prevents finding 01 from recurring after the cleanup lands.
    // The Api copies of the shared dispatch types (DispatchStateBuilder, DispatchLifecycleService,
    // DispatchTemplateResolver, PvcAvailabilityResult) are canonical. K8sJobCreationContext is a
    // private nested record inside DispatchLifecycleService — it appears in reflection but is not
    // a public type and not a duplication concern.
    //
    // All Orchestration copies have been deleted (arch-audit 2026-08-22).
    // This allowlist should now be empty. The test fails if any type name appears
    // in both Api.Dispatch and Orchestration.Dispatch simultaneously.

    [Fact]
    public void ApiDispatch_And_OrchestrationDispatch_ShouldNot_ShareTypeNames()
    {
        // No known survivors — all duplicates have been removed.
        // If a type name appears in both namespaces, it is a new unintentional duplication.
        var knownSurvivors = new HashSet<string>(StringComparer.Ordinal)
        {
            // Empty — all Orchestration.Dispatch copies were deleted in arch-audit 2026-08-22.
            // Do NOT add new entries here — fix the duplication instead.
        };

        var api = Types.InAssembly(ApiAssembly)
            .That().ResideInNamespace("CodingAgent.Api.Dispatch")
            .GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var orch = Types.InAssembly(OrchestrationAssembly)
            .That().ResideInNamespace("CodingAgent.Orchestration.Dispatch")
            .GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var shared = api.Intersect(orch, StringComparer.Ordinal)
            .Where(n => !knownSurvivors.Contains(n))
            .OrderBy(n => n).ToList();

        Assert.True(shared.Count == 0,
            $"Dispatch types duplicated across Api and Orchestration: {string.Join(", ", shared)}. " +
            "The Api copies are canonical (Spec 043 handoff + arch-audit 2026-08-22). " +
            "Delete the Orchestration copies and repoint tests.");
    }

    // ── SyncRoot authorized-consumer allowlist ──────────────────────────
    // Source-scanning test: walks src/ for files that acquire AgentEntry.SyncRoot
    // and asserts every consumer is in the documented allowlist.
    // Cannot use NetArchTest here — lock(x.SyncRoot) is an IL detail invisible
    // to assembly-level rules. See docs/architecture/concurrency-model.md.

    [Fact]
    public void AgentEntry_SyncRoot_ConsumersMatchDocumentedAllowlist()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AgentEntry.cs",                        // declares SyncRoot
            "AgentRegistryService.cs",              // Register(), UpdateHeartbeat(), TransitionStatus()
            "RunLifecycleManager.cs",               // ActiveJobId mutation on assignment/completion
            "AgentOrphanRecoveryService.cs",        // check-and-set ActiveJobId on reconnect; Spec 046 partial migration
            "AgentEndpoints.cs",                    // sets ActiveChatSessionId on chat-resume; Spec 046 partial migration
        };

        var srcDir = Path.Combine(RepoRoot, "src");
        var offenders = Directory
            .EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(".SyncRoot", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!)
            .Where(n => !allowed.Contains(n))
            .Distinct().OrderBy(n => n).ToList();

        Assert.True(offenders.Count == 0,
            $"Undocumented AgentEntry.SyncRoot consumers: {string.Join(", ", offenders)}. " +
            "Add them to the authorized-consumer table in docs/architecture/concurrency-model.md " +
            "and this allowlist, and confirm they do not violate the lock-ordering rules.");
    }

    // ── SyncRoot correctness guard: AgentEntry.ActiveJobId only assigned under lock ──
    // Source-scanning test: verifies that every non-exempt AgentEntry.ActiveJobId assignment
    // in any .cs file under src/ appears inside a lock(...) block.
    //
    // Scope: all *.cs files under src/ (excluding obj/ build artefacts).
    //
    // Exempt bare writes (known-safe off-lock; documented per-line below):
    //   AgentJobLifecycleService.cs  — job-rejection path: `agent.ActiveJobId = null`
    //     These are off-lock by design: job-completion/rejection semantics require clearing
    //     the field quickly before the status transition, and the calling context holds no
    //     other lock that could create a nesting hazard.  They are NOT authorized SyncRoot
    //     consumers (see docs/architecture/concurrency-model.md §Authorized consumers).
    //   AgentHub.Consolidation.cs    — consolidation-complete path: `agent.ActiveJobId = null`
    //     Same rationale: job-completion semantics, intentionally off-lock null-clear.
    //   HubConsolidationOperations.cs — local snapshot clear: `agent.ActiveJobId = null`
    //     Duplicate snapshot update for a non-tracked local reference; same rationale.
    //
    // If null-clears are ever found to be unsafe, remove the relevant entries from the
    // exempt set below and update docs/architecture/concurrency-model.md to document the
    // required locking discipline for completion paths.
    //
    // TODO: [WARNING] nextBraceOpensLock has no guard against malformed/edited code where
    // "lock (...)" appears but its { is never found (e.g. if the file is syntactically broken
    // or an intervening line has an unrelated { before the lock body opens).  In that case
    // nextBraceOpensLock stays armed and binds to the first subsequent { it encounters,
    // silently corrupting lockBraceDepths for the rest of the file.  Replace with a
    // Roslyn-based approach that parses the syntax tree directly for a robust long-term guard.
    //
    // TODO: [WARNING] The brace-depth scanner does not strip block comments (/* ... */) or
    // braces inside string literals.  If a future file adds either construct, the scanner
    // can silently mis-track lock depth.  Switch to a Roslyn-based check if that happens.
    //
    // Implementation: brace-depth tracking. Each lock (...) { increments the lock depth; each
    // closing } decrements it. An ActiveJobId assignment found at lockDepth == 0 is bare (unlocked).
    //
    // See docs/architecture/concurrency-model.md — authorized consumers + release-then-reacquire pattern.

    // Exempt file-name → set of bare `ActiveJobId = <expr>` line contents (trimmed) that are
    // known-safe off-lock.  Two categories are exempt:
    //
    //   (a) Object-initializer / record-`with` / DTO-mapping assignments — these are writes
    //       to a freshly-constructed object (or an immutable clone), not mutations on a shared
    //       live AgentEntry.  No concurrent reader can see the object until construction is
    //       complete, so no lock is required.
    //
    //   (b) Intentional off-lock null-clears in job-completion/rejection paths — these are NOT
    //       authorized SyncRoot consumers (see docs/architecture/concurrency-model.md
    //       §Authorized consumers).  They clear the field as part of job-completion semantics
    //       where the calling context holds no other lock.
    //
    // To add a new exemption: confirm the write falls into one of the above categories and
    // document the rationale in a comment next to the entry.
    private static readonly Dictionary<string, HashSet<string>> ActiveJobIdBareWriteExemptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Category (a): object-initializer / with-expression / DTO-mapping ──

            // DistributedAgentRegistryService — snapshot object-initializer (BuildSnapshot method):
            // constructs a new AgentEntry snapshot; the object is not yet shared.
            // Also: _localSnapshot update via record `with { ActiveJobId = ... }` in UpdateAgentFieldAsync
            // (immutable `with` expression creates a new snapshot record, not a mutation of a live entry).
            // Also: BuildAgentEntryFromHashEntries object-initializer reading from Redis hash.
            ["DistributedAgentRegistryService.cs"] = new(StringComparer.Ordinal)
            {
                "ActiveJobId = activeJobId,",
                "\"activeJobId\" => snapshot with { ActiveJobId = string.IsNullOrEmpty(value) ? null : value },",
                "ActiveJobId = dict.GetValueOrDefault(\"activeJobId\") is { Length: > 0 } aj ? aj : null,",
            },

            // AgentEntryDtoFactory — DTO mapping: reads entry.ActiveJobId into a DTO; no write to a live entry.
            ["AgentEntryDtoFactory.cs"] = new(StringComparer.Ordinal)
            {
                "ActiveJobId = entry.ActiveJobId,",
            },

            // ApiAgentRegistryService — object-initializer building AgentEntry from a DTO (read path, not mutation).
            ["ApiAgentRegistryService.cs"] = new(StringComparer.Ordinal)
            {
                "ActiveJobId = dto.ActiveJobId,",
            },

            // ── Category (b): intentional off-lock null-clears in job-completion paths ──

            // job-rejection path in AgentJobLifecycleService (two identical call sites)
            ["AgentJobLifecycleService.cs"] = new(StringComparer.Ordinal) { "agent.ActiveJobId = null;" },
            // consolidation-complete path in AgentHub.Consolidation
            ["AgentHub.Consolidation.cs"] = new(StringComparer.Ordinal) { "agent.ActiveJobId = null; // local snapshot update" },
            // duplicate local snapshot clear in HubConsolidationOperations
            ["HubConsolidationOperations.cs"] = new(StringComparer.Ordinal) { "agent.ActiveJobId = null;" },
        };

    [Fact]
    public void AgentEntry_ActiveJobId_OnlyAssignedUnderLock_CwideGuard()
    {
        var srcRoot = Path.Combine(RepoRoot, "src");
        var sourceFiles = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        // Violations: (file, 1-based line number) pairs.
        var violations = new List<(string File, int Line)>();

        foreach (var filePath in sourceFiles)
        {
            var fileName = Path.GetFileName(filePath);
            var lines = File.ReadAllLines(filePath);
            ScanFileForBareActiveJobIdAssignments(lines, fileName, filePath, violations);
        }

        Assert.True(violations.Count == 0,
            $"Bare AgentEntry.ActiveJobId assignments found outside lock(SyncRoot) " +
            $"({violations.Count} violation(s)):\n" +
            string.Join("\n", violations.Select(v => $"  {v.File}:{v.Line}")) + "\n" +
            "All non-exempt ActiveJobId mutations must be wrapped in lock(entry.SyncRoot) " +
            "using the release-then-reacquire pattern. " +
            "To exempt a known-safe bare write, add it to ActiveJobIdBareWriteExemptions " +
            "with a comment explaining why it is safe. " +
            "See docs/architecture/concurrency-model.md for the authorized-consumer rules.");
    }

    private static void ScanFileForBareActiveJobIdAssignments(
        string[] lines,
        string fileName,
        string filePath,
        List<(string, int)> violations)
    {
        // Track brace depth globally and lock-brace depth separately.
        // lockBraceDepths holds the brace depth at which each active lock block opened
        // (post-increment, i.e. the depth INSIDE the lock body).
        //
        // Algorithm: lock-statement detection runs BEFORE the brace-counting loop so that
        // when "lock (...) {" appears on a single line the opening { is recognised as a
        // lock brace at the correct post-increment depth.
        //
        // Pop invariant (verified correct; do not swap order):
        //   Push records braceDepth AFTER incrementing for the '{'.
        //   Pop check fires BEFORE decrementing for the '}': at that point braceDepth
        //   still equals the value recorded on push, so the comparison is correct.
        //   Multi-brace lines (e.g. "}) {") are handled because the char loop is
        //   left-to-right: the closing '}' is processed and popped first, then the '{'.
        var braceDepth = 0;
        var lockBraceDepths = new Stack<int>(); // depths (post-{-increment) at which lock blocks opened
        var nextBraceOpensLock = false;         // true after "lock (...)" seen WITHOUT same-line {

        ActiveJobIdBareWriteExemptions.TryGetValue(fileName, out var exemptLines);

        foreach (var (rawLine, index) in lines.Select((l, i) => (l, i)))
        {
            // Strip single-line comments to avoid matching { or } in // ... text.
            // TODO: [WARNING] does not handle /* */ block comments or // inside string literals;
            // if a future refactor adds those to a scanned file, switch to a Roslyn-based check.
            var commentStart = rawLine.IndexOf("//", StringComparison.Ordinal);
            var line = commentStart >= 0 ? rawLine[..commentStart] : rawLine;

            var trimmed = line.Trim();

            // ── Step 1: detect "lock (...)" BEFORE counting braces ──
            // This ensures we know whether an opening { on this line belongs to a lock.
            var lineStartsLock = false; // true if this line contains "lock (...)"
            var sameBraceOnLine = false; // true if the lock { is also on this line
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^lock\s*\("))
            {
                lineStartsLock = true;
                sameBraceOnLine = trimmed.Contains('{');
            }

            // ── Step 2: count braces on this line ──
            foreach (var ch in line)
            {
                if (ch == '{')
                {
                    braceDepth++;
                    // This { opens a lock scope if:
                    //   (a) the previous line had "lock (...)" with no {, OR
                    //   (b) this line itself has "lock (...)" and this { is its brace.
                    // For case (b) we use the lineStartsLock + sameBraceOnLine flags;
                    // we consume the flag so only the first { on a same-line lock is pushed.
                    if (nextBraceOpensLock)
                    {
                        lockBraceDepths.Push(braceDepth);
                        nextBraceOpensLock = false;
                    }
                    else if (lineStartsLock && sameBraceOnLine)
                    {
                        lockBraceDepths.Push(braceDepth);
                        lineStartsLock = false; // consume — only push once per lock statement
                    }
                }
                else if (ch == '}')
                {
                    // Pop invariant: check BEFORE decrement (see header comment).
                    if (lockBraceDepths.Count > 0 && braceDepth == lockBraceDepths.Peek())
                        lockBraceDepths.Pop();
                    braceDepth--;
                }
            }

            // ── Step 3: arm nextBraceOpensLock for the next line if { not yet seen ──
            if (lineStartsLock && !sameBraceOnLine)
                nextBraceOpensLock = true;

            // ── Step 4: detect bare ActiveJobId assignment ──
            // Match both simple assignment (= not followed by = or ?) and null-coalescing
            // assignment (??=), which was previously missed by the [^=?] character class.
            // Pattern: \bActiveJobId\s* followed by either (??=) or (= not followed by =).
            // Note: plain `=?` (conditional assignment) is not a C# operator; the [^=]
            // lookahead on the simple-assignment branch excludes only ==.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    trimmed,
                    @"\bActiveJobId\s*(?:\?\?=|=[^=])")
                && lockBraceDepths.Count == 0)
            {
                // Check against the per-file exemption list (raw trimmed line content from
                // the original line, before comment stripping, to preserve inline comments
                // that are part of the exemption key).
                var rawTrimmed = rawLine.Trim();
                if (exemptLines is null || !exemptLines.Contains(rawTrimmed))
                {
                    violations.Add((filePath, index + 1));
                }
            }
        }
    }

    // ── T9: Agent must not depend on Infrastructure.Persistence ───────────
    // Guard test: ensures the Agent assembly never gains a dependency on the
    // Persistence assembly. The T9 split is complete — this test now uses a
    // typeof() anchor against the real Persistence assembly.
    // Starts green and must stay green. Regression guard, not a red-first test.

    [Fact]
    public void Agent_ShouldNot_DependOnInfrastructurePersistence()
    {
        var result = Types.InAssembly(AgentAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Infrastructure.Persistence")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Agent must not reference Infrastructure.Persistence. " +
            $"The Agent runs in untrusted job pods — Npgsql and EF Core must never " +
            $"appear in its dependency graph. Violating types: {FormatViolations(result)}");
    }

    // ── T9 structural rule: Persistence must not reference Providers in reverse ─
    // Providers references nothing in Persistence. Persistence references Providers (one-way).
    // This test catches if someone accidentally adds a Providers → Persistence dependency.

    [Fact]
    public void InfrastructureProviders_ShouldNot_DependOnInfrastructurePersistence()
    {
        var result = Types.InAssembly(InfrastructureProvidersAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgent.Infrastructure.Persistence")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Infrastructure.Providers must not reference Infrastructure.Persistence. " +
            $"The dependency is one-way: Persistence → Providers. " +
            $"Violating types: {FormatViolations(result)}");
    }

    private static string FormatViolations(TestResult result)
    {
        if (result.IsSuccessful || result.FailingTypes == null)
            return "(none)";

        return string.Join(", ", result.FailingTypes.Select(t => t.FullName));
    }

    // ── T4: Every BackgroundService is registered or explicitly retired ─
    // Prevents a repeat of the HeartbeatMonitorService incident (moved between
    // hosts, silently lost, nine tests kept it green while nothing ran).
    // Source-scanning rather than NetArchTest: AddHostedService patterns vary.

    [Fact]
    public void AllBackgroundServices_AreRegisteredOrRetired()
    {
        // ── Step 1: source-scan src/ for AddHostedService registrations ──
        // (reflection-based assembly scanning is not used here — the T4 check is
        // intentionally source-file-based to catch services that compile but are
        // never registered. The srcAssemblies variable that previously appeared
        // here was dead code and has been removed — T9 2026-08-23.)
        var srcDir = Path.Combine(RepoRoot, "src");

        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);

        // Scan AddHostedService call sites in all production src files
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddHostedService")) continue;

            // Extract type names from AddHostedService<T>() or AddHostedService(sp => new T(...))
            // We look for the type name following AddHostedService< or new in the lambda
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                if (!line.Contains("AddHostedService")) continue;
                // Extract from AddHostedService<TypeName>
                var genericMatch = AddHostedServiceRegex().Match(line);
                if (genericMatch.Success)
                    registeredTypes.Add(genericMatch.Groups[1].Value.Split('.')[^1]);
                // Extract from sp.GetRequiredService<TypeName>() in lambda
                var lambdaMatch = GetRequiredServiceRegex().Match(line);
                if (lambdaMatch.Success)
                    registeredTypes.Add(lambdaMatch.Groups[1].Value.Split('.')[^1]);
                // Extract from new TypeName(
                var newMatch = NewInstanceRegex().Match(line);
                if (newMatch.Success)
                    registeredTypes.Add(newMatch.Groups[1].Value.Split('.')[^1]);
            }
        }

        // ── Step 2: explicitly retired types (deliberate non-registrations with reason) ──
        var retired = new HashSet<string>(StringComparer.Ordinal)
        {
            // Removed in arch-audit wave 1 (2026-08-22). ReconciliationService (JobController)
            // handles timeout enforcement.
            // "HeartbeatMonitorService", // DELETED — do not add back

            // Spec 046: conditionally registered via AddHostedService lambda pattern.
            // When signalr.redis.connectionString is set these run; when absent a NoOpHostedService
            // substitutes. The T4 scanner cannot detect the conditional GetService<T> lambda pattern
            // so these are listed here as "conditionally registered, not retired".
            "AgentRegistryCleanupService",
            "RunServiceCleanupService",

            // Spec 048 Phase 2: WorkItemMetricsBackgroundService was deleted (dead code since
            // Spec 047, replaced by WorkItemCountsPoller in the Scheduler which polls
            // GET /api/work-items/counts-by-status). No allowlist entry is needed — the scanner
            // cannot discover a type that no longer exists in src/.

            // Spec 047: LoopStatusPollingService is registered in the WebUI via AddHostedService
            // with a cast: AddHostedService(sp => (LoopStatusPollingService)sp.GetRequiredService<ILoopStatusService>()).
            // The T4 scanner does not detect the cast pattern — service is actively registered.
            "LoopStatusPollingService",

            // WorkItemDispatchService is registered via AddHostedService lambda pattern:
            //   services.AddHostedService(sp => sp.GetRequiredService<WorkItemDispatchService>())
            // The T4 scanner only detects AddHostedService<T>() (generic form), not the lambda
            // pattern that resolves a pre-registered singleton. The service IS actively registered
            // in ApiServiceCollectionExtensions.AddApiOrchestration.
            "WorkItemDispatchService",
        };

        // ── Step 3: find all concrete BackgroundService subclasses in src files ──
        var unregistered = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains(": BackgroundService") && !content.Contains(": LeaderElectedPollingService")) continue;

            var classMatch = ClassNameRegex().Match(content);
            if (!classMatch.Success) continue;

            var typeName = classMatch.Groups[1].Value;
            if (!registeredTypes.Contains(typeName) && !retired.Contains(typeName))
                unregistered.Add($"{typeName} ({Path.GetFileName(file)})");
        }

        Assert.True(unregistered.Count == 0,
            $"BackgroundService subclasses not registered in any DI container or retired allowlist: " +
            $"{string.Join(", ", unregistered)}. " +
            "Either register the service or add it to the 'retired' allowlist with a reason. " +
            "See docs/architecture: the HeartbeatMonitorService incident.");
    }

    // ── Positive control for T4: proves the scanner finds a known-registered service ──
    [Fact]
    public void T4_PositiveControl_PipelineLoopService_IsDetectedByScanner()
    {
        // PipelineLoopService is registered with AddHostedService in
        // CodingAgent.Scheduler/SchedulerServiceCollectionExtensions.cs (Spec 047).
        // If the scanner does not find it, the T4 test above is vacuous.
        var srcDir = Path.Combine(RepoRoot, "src");
        var registeredTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains("AddHostedService")) continue;

            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                if (!line.Contains("AddHostedService")) continue;
                var match = GetRequiredServiceRegex().Match(line);
                if (match.Success)
                    registeredTypes.Add(match.Groups[1].Value.Split('.')[^1]);
            }
        }

        Assert.Contains("PipelineLoopService", registeredTypes);
    }

    // ── T5: Monolith owns no database ──────────────────────────────────────
    // Spec 045 end-state, completed by Spec 048 Phase 2: CodingAgent.Web has no EF Core, no
    // PipelineDbContext, no Npgsql in its own source. This source-scan is the fast, precise guard
    // (it names the offending file); WebHost_Closure_IsPersistenceFree above is the assembly-level
    // counterpart that also catches a transitive EF pull through a referenced project.

    [Fact]
    public void Monolith_ShouldNot_OwnDatabase()
    {
        var srcDir = Path.Combine(RepoRoot, "src", "CodingAgent.Web");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var content = File.ReadAllText(file);
            if (content.Contains("PipelineDbContext") ||
                content.Contains("UseNpgsql") ||
                content.Contains("EntityFrameworkCore"))
            {
                violations.Add(Path.GetRelativePath(srcDir, file));
            }
        }

        Assert.True(violations.Count == 0,
            $"Monolith (CodingAgent.Web) must not own a database. " +
            $"Files with EF Core / Npgsql / PipelineDbContext references: " +
            $"{string.Join(", ", violations)}. " +
            "Complete T8 to remove these references.");
    }

    // ── Spec 048 Phase 4: the legacy monolith namespace prefix is fully retired ──
    // After the rename every type lives under CodingAgent.* (KiroCliLib excepted). No file may
    // reintroduce the old monolith-era prefix (assembled below as `forbidden`) — as a namespace,
    // using, type reference, or string. This source-scan is the guardrail that keeps the rename
    // from creeping back (a new file under the old namespace, a stray using, a copied snippet).
    // The token is assembled from fragments so THIS file never spells it out and never matches
    // itself.

    [Fact]
    public void NoSource_ReintroducesLegacyMonolithNamespacePrefix()
    {
        var forbidden = "CodingAgent" + "WebUI";
        var roots = new[] { Path.Combine(RepoRoot, "src"), Path.Combine(RepoRoot, "tests") };
        var offenders = new List<string>();

        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            {
                if (File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        Assert.True(offenders.Count == 0,
            $"The legacy '{forbidden}' prefix is fully retired (Spec 048 Phase 4), but these " +
            $"files still reference it: {string.Join(", ", offenders)}. " +
            "Rename to the CodingAgent.* scheme — the prefix must not come back.");
    }
}

// Source-generated regexes (SYSLIB1045) — must be in a partial class
public partial class LayerBoundaryTests
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:public|internal)\s+sealed?\s+(?:partial\s+)?class\s+([A-Za-z0-9_]+)")]
    private static partial System.Text.RegularExpressions.Regex ClassNameRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"AddHostedService<([A-Za-z0-9_.]+)>")]
    private static partial System.Text.RegularExpressions.Regex AddHostedServiceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"GetRequiredService<([A-Za-z0-9_.]+)>\s*\(\)")]
    private static partial System.Text.RegularExpressions.Regex GetRequiredServiceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"new ([A-Za-z0-9_.]+)\s*\(")]
    private static partial System.Text.RegularExpressions.Regex NewInstanceRegex();
}
