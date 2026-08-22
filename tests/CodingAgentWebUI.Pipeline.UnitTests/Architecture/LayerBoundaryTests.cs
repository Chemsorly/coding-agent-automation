using NetArchTest.Rules;

namespace CodingAgentWebUI.Pipeline.UnitTests.Architecture;

/// <summary>
/// Enforces layer dependency rules to prevent architectural erosion.
/// These tests encode the project dependency direction rules from the architecture analysis:
/// - Pipeline must NOT reference Infrastructure or Orchestration
/// - Infrastructure must NOT reference Orchestration or WebUI
/// - Agent projects must NOT reference Orchestration
/// - Agent (main) must NOT reference Infrastructure (confirmed violation — tracked here to prevent regression)
/// </summary>
public class LayerBoundaryTests
{
    // Assembly anchors for each layer
    private static readonly System.Reflection.Assembly PipelineAssembly =
        typeof(Pipeline.Services.PipelineOrchestrationService).Assembly;

    private static readonly System.Reflection.Assembly InfrastructureAssembly =
        typeof(CodingAgentWebUI.Infrastructure.GitHub.GitHubRepositoryProvider).Assembly;

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
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOn("CodingAgentWebUI.Orchestration")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Infrastructure must not reference Orchestration. Violating types: {FormatViolations(result)}");
    }

    [Fact]
    public void Infrastructure_ShouldNot_DependOnWebUI()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "CodingAgentWebUI.Hubs",
                "CodingAgentWebUI.Services",
                "CodingAgentWebUI.Components",
                "CodingAgentWebUI.Models")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Infrastructure must not reference WebUI namespaces. Violating types: {FormatViolations(result)}");
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
    // Prevents finding 01 from recurring after the cleanup in the 041-045 arc.
    // The Api copies of ConsolidationWorkItemDispatchService and its Dependencies
    // record are canonical. This test tracks the *known* surviving duplicates (which
    // serve the Orchestration DispatchService / Job Controller path) and fails if
    // NEW duplicates are introduced.
    //
    // Known survivors (Orchestration.DispatchService depends on these):
    //   DispatchLifecycleService, DispatchStateBuilder, DispatchTemplateResolver,
    //   K8sJobCreationContext, PvcAvailabilityResult
    //
    // Remediation: once the Job Controller is fully API-backed and DispatchService
    // is removed from Orchestration, delete this allowlist and assert shared.Count == 0.

    [Fact]
    public void ApiDispatch_And_OrchestrationDispatch_ShouldNot_ShareTypeNames()
    {
        // Types that are knowingly duplicated because Orchestration.DispatchService
        // (Job Controller path) depends on the Orchestration copies.
        // Do NOT add new entries here — fix the duplication instead.
        var knownSurvivors = new HashSet<string>(StringComparer.Ordinal)
        {
            "DispatchLifecycleService",
            "DispatchStateBuilder",
            "DispatchTemplateResolver",
            "K8sJobCreationContext",
            "PvcAvailabilityResult",
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
            $"NEW dispatch types duplicated across Api and Orchestration: {string.Join(", ", shared)}. " +
            "The Api copies are canonical (Spec 043 handoff). Delete the Orchestration copies " +
            "and repoint tests rather than letting the two drift. " +
            $"Known survivors (Job Controller path): {string.Join(", ", knownSurvivors)}");
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
            "HeartbeatMonitorService.cs",           // delegates to sweep phases
            "RunLifecycleManager.cs",               // ActiveJobId mutation on assignment/completion
            "DisconnectedAgentSweepPhase.cs",       // reads DisconnectedAt; clears ActiveJobId
            "ProgressTimeoutSweepPhase.cs",         // reads BusySince; clears ActiveJobId on timeout
            "OrphanRestoredJobSweepPhase.cs",       // reads OrphanRestoredAt; clears ActiveJobId
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

    private static string FormatViolations(TestResult result)
    {
        if (result.IsSuccessful || result.FailingTypes == null)
            return "(none)";

        return string.Join(", ", result.FailingTypes.Select(t => t.FullName));
    }
}
