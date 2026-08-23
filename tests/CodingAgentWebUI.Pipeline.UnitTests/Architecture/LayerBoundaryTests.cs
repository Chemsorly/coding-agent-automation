using NetArchTest.Rules;

namespace CodingAgentWebUI.Pipeline.UnitTests.Architecture;

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
public class LayerBoundaryTests
{
    // Assembly anchors for each layer
    private static readonly System.Reflection.Assembly PipelineAssembly =
        typeof(Pipeline.Services.PipelineOrchestrationService).Assembly;

    // T9 split: Infrastructure is now two assemblies.
    // Providers: no EF Core, no Npgsql — safe for untrusted agent pods.
    // Persistence: EF Core + Npgsql — API and orchestrator only.
    private static readonly System.Reflection.Assembly InfrastructureProvidersAssembly =
        typeof(CodingAgentWebUI.Infrastructure.GitHub.GitHubRepositoryProvider).Assembly;

    private static readonly System.Reflection.Assembly InfrastructurePersistenceAssembly =
        typeof(CodingAgentWebUI.Infrastructure.Persistence.PipelineDbContext).Assembly;

    private static readonly System.Reflection.Assembly AgentAssembly =
        typeof(CodingAgentWebUI.Agent.WorkItemAgentService).Assembly;

    private static readonly System.Reflection.Assembly AgentKiroCliAssembly =
        typeof(CodingAgentWebUI.Agent.KiroCli.KiroCliAgentProvider).Assembly;

    private static readonly System.Reflection.Assembly AgentOpenCodeAssembly =
        typeof(CodingAgentWebUI.Agent.OpenCode.OpenCodeAgentProvider).Assembly;

    private static readonly System.Reflection.Assembly ApiAssembly =
        typeof(CodingAgentWebUI.Api.ApiHostMarker).Assembly;

    private static readonly System.Reflection.Assembly OrchestrationAssembly =
        typeof(CodingAgentWebUI.Orchestration.RunLifecycleManager).Assembly;

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

    [Fact]
    public void Pipeline_ShouldNot_DependOnInfrastructure()
    {
        var result = Types.InAssembly(PipelineAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgentWebUI.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Pipeline layer must not reference Infrastructure. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void Pipeline_ShouldNot_DependOnOrchestration()
    {
        var result = Types.InAssembly(PipelineAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgentWebUI.Orchestration")
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
                "CodingAgentWebUI.Hubs",
                "CodingAgentWebUI.Services",
                "CodingAgentWebUI.Components",
                "CodingAgentWebUI.Models")
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
                .HaveDependencyOn("CodingAgentWebUI.Orchestration")
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
                    "CodingAgentWebUI.Hubs",
                    "CodingAgentWebUI.Services",
                    "CodingAgentWebUI.Components",
                    "CodingAgentWebUI.Models")
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
            .HaveDependencyOn("CodingAgentWebUI.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Agent.KiroCli must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void AgentOpenCode_ShouldNot_DependOnOrchestration()
    {
        var result = Types.InAssembly(AgentOpenCodeAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgentWebUI.Orchestration")
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
            .HaveDependencyOn("CodingAgentWebUI.Orchestration")
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
            .HaveDependencyOn("CodingAgentWebUI.Pipeline.CodeReview")
            .GetTypes();

        Assert.NotEmpty(result);
    }

    // ── Dispatch duplication guard ──────────────────────────────────────
    // Prevents finding 01 from recurring after the cleanup lands.
    // The Api copies of ConsolidationWorkItemDispatchService and the shared dispatch
    // types (DispatchStateBuilder, DispatchLifecycleService, DispatchTemplateResolver,
    // PvcAvailabilityResult) are canonical. K8sJobCreationContext is a private nested
    // record inside DispatchLifecycleService — it appears in reflection but is not
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
            .That().ResideInNamespace("CodingAgentWebUI.Api.Dispatch")
            .GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var orch = Types.InAssembly(OrchestrationAssembly)
            .That().ResideInNamespace("CodingAgentWebUI.Orchestration.Dispatch")
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
            "JobDeduplicationGuardService.cs",      // SelectAgent() — nested inside _selectionLock
            "RunLifecycleManager.cs",               // ActiveJobId mutation on assignment/completion
            "AgentOrphanRecoveryService.cs",        // check-and-set ActiveJobId on reconnect
            "AgentEndpoints.cs",                    // sets ActiveChatSessionId on chat-resume
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
            .HaveDependencyOn("CodingAgentWebUI.Infrastructure.Persistence")
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
            .HaveDependencyOn("CodingAgentWebUI.Infrastructure.Persistence")
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
                var genericMatch = System.Text.RegularExpressions.Regex.Match(line, @"AddHostedService<([A-Za-z0-9_.]+)>");
                if (genericMatch.Success)
                    registeredTypes.Add(genericMatch.Groups[1].Value.Split('.')[^1]);
                // Extract from sp.GetRequiredService<TypeName>() in lambda
                var lambdaMatch = System.Text.RegularExpressions.Regex.Match(line, @"GetRequiredService<([A-Za-z0-9_.]+)>\s*\(\)");
                if (lambdaMatch.Success)
                    registeredTypes.Add(lambdaMatch.Groups[1].Value.Split('.')[^1]);
                // Extract from new TypeName(
                var newMatch = System.Text.RegularExpressions.Regex.Match(line, @"new ([A-Za-z0-9_.]+)\s*\(");
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
        };

        // ── Step 3: find all concrete BackgroundService subclasses in src files ──
        var unregistered = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var content = File.ReadAllText(file);
            if (!content.Contains(": BackgroundService") && !content.Contains(": LeaderElectedPollingService")) continue;

            var classMatch = System.Text.RegularExpressions.Regex.Match(content,
                @"(?:public|internal)\s+sealed?\s+(?:partial\s+)?class\s+([A-Za-z0-9_]+)");
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
        // ServiceCollectionExtensions.PipelineBackgroundServices.cs.
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
                var match = System.Text.RegularExpressions.Regex.Match(line,
                    @"GetRequiredService<([A-Za-z0-9_.]+)>\s*\(\)");
                if (match.Success)
                    registeredTypes.Add(match.Groups[1].Value.Split('.')[^1]);
            }
        }

        Assert.Contains("PipelineLoopService", registeredTypes);
    }

    // ── T5: Monolith owns no database ──────────────────────────────────────
    // Spec 045 end-state: CodingAgentWebUI has no EF Core, no PipelineDbContext, no Npgsql.
    // Currently still failing (T8 not yet complete). Skipped until T8 lands.
    // When T8 is done: unskip this test and remove the Skip attribute.

    [Fact]
    public void Monolith_ShouldNot_OwnDatabase()
    {
        // Load the monolith assembly by scanning for it in the test binary directory
        var monolithPath = Path.Combine(
            AppContext.BaseDirectory.Replace("Pipeline.UnitTests", "").TrimEnd(Path.DirectorySeparatorChar),
            "..", "..", "..", "..", "..", "src", "CodingAgentWebUI", "bin", "Debug", "net10.0",
            "CodingAgentWebUI.dll");

        var srcDir = Path.Combine(RepoRoot, "src", "CodingAgentWebUI");
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
            $"Monolith (CodingAgentWebUI) must not own a database. " +
            $"Files with EF Core / Npgsql / PipelineDbContext references: " +
            $"{string.Join(", ", violations)}. " +
            "Complete T8 to remove these references.");
    }
}
