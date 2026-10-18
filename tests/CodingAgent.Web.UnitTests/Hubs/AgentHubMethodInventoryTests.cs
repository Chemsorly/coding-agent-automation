using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Spec 052 Req 14.10 (Task 5.4): the agent hub's methods, pinned in <c>agent-hub-methods.snapshot.txt</c>, so a new
/// method shows up in the PR diff. A method that addresses a job (a <see cref="JobId"/>, run or agent ID) must carry
/// <see cref="RequiresActiveJobAttribute"/>, which makes <see cref="AgentAuthorizationFilter"/> check that the
/// calling agent owns that job (T6, <c>AgentAuthorizationFilterInvokeTests</c>), or have an exemption with a reason.
/// </summary>
public sealed class AgentHubMethodInventoryTests
{
    private const string SnapshotFile = "agent-hub-methods.snapshot.txt";

    /// <summary>ID-taking methods without the job-ownership check, each with the reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        [nameof(AgentHub.AgentReady)] =
            "Addresses an agent, not a job: the hub ignores the call unless the caller's connection owns that agent " +
            "(AgentHubBehaviorTests.AgentReady_AgentIdMismatch_RejectsWithoutSignaling).",
        [nameof(AgentHub.DeregisterAgent)] =
            "Addresses an agent, not a job: the hub deregisters only the agent the caller's connection owns " +
            "(AgentHubBehaviorTests.DeregisterAgent_AgentIdMismatch_RejectsWithoutDeregistering).",
    };

    private static IReadOnlyList<MethodInfo> HubMethods =>
        typeof(AgentHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.Name is not (nameof(AgentHub.OnConnectedAsync) or nameof(AgentHub.OnDisconnectedAsync)))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();

    private static bool RequiresActiveJob(MethodInfo m) => m.GetCustomAttribute<RequiresActiveJobAttribute>() is not null;

    [Fact]
    public void HubMethods_MatchTheSnapshot()
    {
        var actual = string.Join("\n", HubMethods.Select(Describe)) + "\n";
        var snapshotPath = Path.Combine(SourceDirectory(), SnapshotFile);
        var expected = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath).ReplaceLineEndings("\n") : "";

        if (actual != expected)
        {
            File.WriteAllText(Path.ChangeExtension(snapshotPath, ".received.txt"), actual);
            actual.Should().Be(expected,
                $"the agent hub's methods changed. Review {Path.GetFileName(Path.ChangeExtension(snapshotPath, ".received.txt"))} " +
                $"next to the snapshot and, if every line is intended, copy it over {SnapshotFile}");
        }
    }

    [Fact]
    public void EveryMethodThatAddressesAJob_ChecksOwnership_OrHasAnExemption()
    {
        var unchecked_ = HubMethods
            .Where(m => TakesAnId(m) && !RequiresActiveJob(m) && !Exempt.ContainsKey(m.Name))
            .Select(Describe);

        string.Join("\n", unchecked_).Should().BeEmpty(
            "a hub method that takes a job, run or agent ID needs [RequiresActiveJob] (T6) or an exemption with a reason");
        Exempt.Keys.Where(name => HubMethods.All(m => m.Name != name || RequiresActiveJob(m) || !TakesAnId(m)))
            .Should().BeEmpty("an exemption for a method that is gone, checks ownership or takes no ID is stale");
    }

    [Fact]
    public void OwnershipCheckedMethods_TakeTheJobIdFirst()
    {
        // AgentAuthorizationFilter reads the job from the first argument.
        HubMethods.Where(RequiresActiveJob)
            .Where(m => m.GetParameters() is not [{ ParameterType: var first }, ..] || first != typeof(JobId))
            .Select(Describe)
            .Should().BeEmpty();
    }

    private static bool TakesAnId(MethodInfo method) =>
        method.GetParameters().Any(p =>
            p.ParameterType == typeof(JobId) || p.ParameterType == typeof(RunId) || p.ParameterType == typeof(AgentId)
            || (p.ParameterType == typeof(string) && p.Name is { } name
                && (name.Equals("jobId", StringComparison.Ordinal) || name.Equals("runId", StringComparison.Ordinal)
                    || name.Equals("workItemId", StringComparison.Ordinal) || name.Equals("agentId", StringComparison.Ordinal))));

    private static string Describe(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => TypeName(p.ParameterType)))})"
        + (RequiresActiveJob(method) ? " [RequiresActiveJob]" : "");

    private static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return TypeName(inner) + "?";
        if (!type.IsGenericType)
            return type.Name;
        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
