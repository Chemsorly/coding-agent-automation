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
}
