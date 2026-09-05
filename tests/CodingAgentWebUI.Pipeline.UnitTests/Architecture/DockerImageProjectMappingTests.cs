using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.UnitTests.Architecture;

/// <summary>
/// Spec 048 Phase 3 — CI Docker path filtering.
///
/// Cross-checks <c>.github/docker-image-projects.json</c> (the source of truth the CI detect job
/// uses to decide which Docker images to build) against the REAL <c>.csproj</c> ProjectReference
/// graph. Each image's declared <c>projects</c> list MUST equal the full transitive closure of its
/// <c>entryProject</c>. This is the MANDATORY drift guard the refactoring plan calls for: without it,
/// a hand-maintained mapping silently rots as the dependency graph evolves — and a stale mapping ships
/// stale images (an image whose real dependency changed but whose filter entry no longer lists it).
/// </summary>
public partial class DockerImageProjectMappingTests
{
    private static readonly string RepoRoot = FindRepoRoot(AppContext.BaseDirectory);
    private static readonly string MapPath = Path.Combine(RepoRoot, ".github", "docker-image-projects.json");

    // The 10 product images the docker-build matrix ships. If this diverges from the mapping,
    // the filter would silently never build (or over-build) an image. Kept here as an independent
    // control so a mapping edit that drops/adds an image is caught.
    private static readonly string[] ExpectedTags =
    {
        "coding-agent-webui", "coding-agent-api", "coding-agent-jobcontroller", "coding-agent-scheduler",
        "coding-agent-kiro-dotnet10", "coding-agent-kiro-python312", "coding-agent-kiro-java21",
        "coding-agent-opencode-dotnet10", "coding-agent-opencode-java21", "coding-agent-opencode-python312",
    };

    [Fact]
    public void EveryImage_ProjectsMatch_RealCsprojClosure()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(MapPath));

        var failures = new List<string>();
        foreach (var img in doc.RootElement.GetProperty("images").EnumerateArray())
        {
            var tag = img.GetProperty("tag").GetString()!;
            var entry = img.GetProperty("entryProject").GetString()!;
            var declared = img.GetProperty("projects").EnumerateArray()
                .Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

            var entryCsproj = FindCsproj(entry);
            if (entryCsproj is null)
            {
                failures.Add($"[{tag}] entryProject '{entry}' has no .csproj under src/");
                continue;
            }

            var computed = ComputeClosure(entryCsproj);

            var missingFromJson = computed.Except(declared).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var extraInJson = declared.Except(computed).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (missingFromJson.Count > 0 || extraInJson.Count > 0)
            {
                failures.Add(
                    $"[{tag}] entry={entry}: " +
                    $"missing-from-json=[{string.Join(",", missingFromJson)}] " +
                    $"extra-in-json=[{string.Join(",", extraInJson)}]");
            }
        }

        Assert.True(failures.Count == 0,
            "docker-image-projects.json is out of sync with the real .csproj ProjectReference graph. " +
            "Each image's 'projects' must be the FULL transitive closure of its 'entryProject' " +
            "(regenerate it — do not hand-tweak). Drift:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Mapping_CoversExactly_TheTenProductImages()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(MapPath));
        var tags = doc.RootElement.GetProperty("images").EnumerateArray()
            .Select(i => i.GetProperty("tag").GetString()!).ToList();

        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count()); // no duplicate tags
        Assert.Equal(
            ExpectedTags.OrderBy(x => x, StringComparer.Ordinal),
            tags.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Mapping_ReferencesOnlyExistingFiles()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(MapPath));
        var missing = new List<string>();

        foreach (var img in doc.RootElement.GetProperty("images").EnumerateArray())
        {
            var tag = img.GetProperty("tag").GetString()!;

            var df = img.GetProperty("dockerfile").GetString()!;
            if (!File.Exists(Path.Combine(RepoRoot, df))) missing.Add($"[{tag}] dockerfile {df}");

            if (FindCsproj(img.GetProperty("entryProject").GetString()!) is null)
                missing.Add($"[{tag}] entryProject csproj");

            if (img.TryGetProperty("extraPaths", out var eps))
                foreach (var ep in eps.EnumerateArray().Select(e => e.GetString()!))
                    if (!File.Exists(Path.Combine(RepoRoot, ep))) missing.Add($"[{tag}] extraPath {ep}");
        }

        foreach (var g in doc.RootElement.GetProperty("globalTriggers").EnumerateArray().Select(e => e.GetString()!))
            if (!File.Exists(Path.Combine(RepoRoot, g))) missing.Add($"globalTrigger {g}");

        Assert.True(missing.Count == 0,
            "docker-image-projects.json references files that do not exist: " + string.Join(", ", missing));
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static HashSet<string> ComputeClosure(string entryCsproj)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(entryCsproj);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var name = Path.GetFileNameWithoutExtension(path);
            if (!seen.Add(name)) continue;
            if (!File.Exists(path)) continue;

            var dir = Path.GetDirectoryName(path)!;
            foreach (Match m in ProjectRefRegex().Matches(File.ReadAllText(path)))
            {
                var rel = m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)
                                           .Replace('/', Path.DirectorySeparatorChar);
                queue.Enqueue(Path.GetFullPath(Path.Combine(dir, rel)));
            }
        }
        return seen;
    }

    // Convention across this repo: project name == folder name == csproj file name.
    private static string? FindCsproj(string projectName)
    {
        var p = Path.Combine(RepoRoot, "src", projectName, projectName + ".csproj");
        return File.Exists(p) ? p : null;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (dir.GetFiles("CodingAgentAutomation.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not find repo root from '{start}'");
    }

    [GeneratedRegex("ProjectReference\\s+Include=\"([^\"]+)\"")]
    private static partial Regex ProjectRefRegex();
}
