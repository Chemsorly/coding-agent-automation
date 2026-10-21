using System.Text.Json;
using CodingAgent.Pipeline;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// What an agent received before spec 053 for one template × task type of a <c>LegacyRoutingScenario</c>, as the
/// pre-053 code computed it (spec 053 req 7.3, 7.14). Null profile fields: no profile ran that work.
/// </summary>
/// <param name="ProfileId">The profile the dispatch picked and the claim kept.</param>
/// <param name="Model">The agent provider config's model, as stored (blank stays blank).</param>
/// <param name="Effort">The agent provider config's effort, as stored.</param>
/// <param name="QualityGates">Ids of the quality gate configs the claim resolved, sorted.</param>
/// <param name="Reviewers">Ids of the reviewer configs the claim resolved, sorted.</param>
/// <param name="McpServers">Names of the MCP servers the assignment carried, sorted.</param>
/// <param name="OperatorTriageClaimProfileId">Triage only: the profile an operator triage's claim re-resolved.</param>
/// <param name="OperatorTriageQualityGates">Triage only: the quality gates of that claim.</param>
public sealed record GoldenCell(
    string Scenario,
    string Template,
    string TaskType,
    string? ProfileId,
    string? ProfileName,
    string? JobTemplateKey,
    string? Model,
    string? Effort,
    IReadOnlyList<string> QualityGates,
    IReadOnlyList<string> Reviewers,
    IReadOnlyList<string> McpServers,
    string? OperatorTriageClaimProfileId = null,
    IReadOnlyList<string>? OperatorTriageQualityGates = null);

/// <summary>
/// The agent provider config an agent looked up by the assignment's <c>AgentProviderConfigId</c> before spec 053.
/// Null fields: the claim found no config, so no agent ran.
/// </summary>
public sealed record GoldenAgentConfig(
    string Scenario,
    string Template,
    string TaskType,
    string? ProviderType,
    IReadOnlyDictionary<string, string>? Settings);

/// <summary>
/// The golden files of spec 053 task 3.2, written once from the pre-053 code by <c>TodayRoutesGoldenTests</c> with
/// <c>GOLDEN_WRITE=1</c> and never regenerated. They are copied to the test output; writing goes to the source folder.
/// </summary>
public static class GoldenFiles
{
    public const string TodayRoutes = "today-routes.golden.json";
    public const string Assignment = "assignment.golden.json";

    private const string Folder = "AgentRouting/Golden";

    private static readonly JsonSerializerOptions Options = new(PipelineJsonOptions.Default) { WriteIndented = true };

    /// <summary>True when the run should write the golden files instead of comparing with them.</summary>
    public static bool WriteMode => Environment.GetEnvironmentVariable("GOLDEN_WRITE") == "1";

    public static IReadOnlyList<T> Read<T>(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, Folder, name);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), PipelineJsonOptions.Lenient)
            ?? throw new InvalidOperationException($"{path} is empty");
    }

    public static void Write<T>(string name, IReadOnlyList<T> rows)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodingAgent.Api.IntegrationTests.csproj")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("The test project folder was not found above " + AppContext.BaseDirectory);

        var target = Path.Combine(directory.FullName, Folder);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, name), JsonSerializer.Serialize(rows, Options) + "\n");
    }
}
