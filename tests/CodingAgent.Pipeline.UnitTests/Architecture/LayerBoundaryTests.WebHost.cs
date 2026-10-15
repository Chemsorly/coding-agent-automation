namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Spec 051 4.2: the web host calls the Pipeline API as its signed-in users (<c>/api/ui</c>, <c>/hubs/ui</c>) and
/// holds no domain logic, provider client or agent API key. Its closure therefore contains no server project and
/// none of the libraries that talk to GitHub, GitLab, git, Kubernetes or images.
/// </summary>
public partial class LayerBoundaryTests
{
    private static readonly string[] WebForbiddenProjects =
    [
        "CodingAgent.Pipeline", "CodingAgent.Orchestration", "CodingAgent.AgentGateway", "CodingAgent.Api.Client",
        "CodingAgent.Infrastructure.Providers", "CodingAgent.Kubernetes", PersistenceAssemblyName,
    ];

    private static readonly string[] WebForbiddenPackages = ["KubernetesClient", "Octokit", "NGitLab", "LibGit2Sharp", "NetVips"];

    [Fact]
    public void WebHost_ProjectReferenceClosure_ContainsNoServerProject()
    {
        var closure = ProjectReferenceClosure(SrcProjectReferenceGraph(), "CodingAgent.Web");

        Assert.Contains("CodingAgent.Contracts", closure); // positive control: the walk sees the web's references
        var offenders = closure.Intersect(WebForbiddenProjects).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.True(offenders.Count == 0,
            $"The web host reaches server projects through its ProjectReferences: {string.Join(", ", offenders)}. " +
            "Call the API's /api/ui surface instead (spec 051 4.2).");
    }

    [Fact]
    public void WebHost_AssemblyClosure_ContainsNoServerProject()
    {
        var offenders = WebForbiddenProjects.SelectMany(p => ClosureEdgesTo("CodingAgent.Web", p)).ToList();
        Assert.True(offenders.Count == 0, $"Web host closure references server assemblies: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void WebHost_ProjectClosure_ReferencesNoProviderOrClusterLibrary()
    {
        var projects = ProjectReferenceClosure(SrcProjectReferenceGraph(), "CodingAgent.Web").Append("CodingAgent.Web");
        var csprojByName = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal);

        var offenders = projects
            .Where(csprojByName.ContainsKey)
            .SelectMany(p => System.Xml.Linq.XDocument.Load(csprojByName[p]).Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(package => WebForbiddenPackages.Contains(package))
                .Select(package => $"{p} -> {package}"))
            .ToList();
        Assert.True(offenders.Count == 0, $"The web host's closure pulls in server libraries: {string.Join(", ", offenders)}");
    }

    // ── Spec 051 5.3: the WebAssembly-ready gate ──
    // The pages, layout, UI services and UI clients live in CodingAgent.Web.Client so they can later run in the
    // browser unchanged. It references Contracts only and nothing of ASP.NET Core that needs a server.

    /// <summary>The ASP.NET Core assemblies that also run in the browser: components, authorization and the SignalR client.</summary>
    private static readonly string[] BrowserSafeAspNetCoreAssemblies =
    [
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Web", "Microsoft.AspNetCore.Components.Forms",
        "Microsoft.AspNetCore.Components.Authorization", "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Metadata",
        "Microsoft.AspNetCore.SignalR.Client", "Microsoft.AspNetCore.SignalR.Client.Core", "Microsoft.AspNetCore.SignalR.Common",
        "Microsoft.AspNetCore.SignalR.Protocols.Json", "Microsoft.AspNetCore.Http.Connections.Client",
        "Microsoft.AspNetCore.Http.Connections.Common",
    ];

    [Fact]
    public void WebClient_ProjectReferenceClosure_IsContractsOnly()
    {
        var graph = SrcProjectReferenceGraph();
        Assert.Contains("CodingAgent.Web.Client", graph.Keys);

        var closure = ProjectReferenceClosure(graph, "CodingAgent.Web.Client");
        var offenders = closure.Except(["CodingAgent.Contracts", "CodingAgent.Pipeline.CodeReview"]).ToList();
        Assert.True(offenders.Count == 0,
            $"CodingAgent.Web.Client must reference Contracts only (spec 051 D9), but reaches: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void WebClient_HasNoServerFrameworkReference()
    {
        var projects = new[] { "CodingAgent.Web.Client", "CodingAgent.Contracts", "CodingAgent.Pipeline.CodeReview" };
        var offenders = projects
            .Select(p => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", p), p + ".csproj").Single())
            .Where(path => System.Xml.Linq.XDocument.Load(path).Descendants()
                .Any(e => e.Name.LocalName == "FrameworkReference"))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();
        Assert.True(offenders.Count == 0,
            $"A FrameworkReference (Microsoft.AspNetCore.App) is server-only, but these projects of the Web.Client closure have one: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void WebClient_ReferencesOnlyBrowserSafeAspNetCoreAssemblies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CodingAgent.Web.Client.dll");
        Assert.True(File.Exists(path), $"CodingAgent.Web.Client.dll not found at {path} — build first.");

        var references = System.Reflection.Assembly.LoadFrom(path).GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Contains("Microsoft.AspNetCore.Components", references); // positive control

        var offenders = references
            .Where(name => name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && !BrowserSafeAspNetCoreAssemblies.Contains(name))
            .Concat(references.Where(name => name.StartsWith("CodingAgent.", StringComparison.Ordinal)
                                             && name is not ("CodingAgent.Contracts" or "CodingAgent.Pipeline.CodeReview")))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"CodingAgent.Web.Client references assemblies that need a server: {string.Join(", ", offenders)}. " +
            "Keep server code in CodingAgent.Web and reach it through an interface the host registers (spec 051 5.2).");
    }
}
