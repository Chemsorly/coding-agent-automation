using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Source-scan guards for the agent process hardening startup sequence (Issue #4078).
/// </summary>
public partial class LayerBoundaryTests
{
    [Fact]
    public void AgentProgram_HardensTheProcess_InTheRequiredOrder()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "CodingAgent.Agent", "Program.cs"));

        // All four calls must be present.
        var idxMark = source.IndexOf("AgentProcessHardening.MarkProcessNotDumpable()", StringComparison.Ordinal);
        var idxResolve = source.IndexOf("AgentStartupConfig.ResolveAsync(args)", StringComparison.Ordinal);
        var idxRemove = source.IndexOf("AgentProcessHardening.RemoveSecretsFromEnvironment()", StringComparison.Ordinal);
        var idxBuilder = source.IndexOf("WebApplication.CreateBuilder(args)", StringComparison.Ordinal);

        idxMark.Should().BeGreaterThan(-1, "MarkProcessNotDumpable() must be present");
        idxResolve.Should().BeGreaterThan(-1, "ResolveAsync(args) must be present");
        idxRemove.Should().BeGreaterThan(-1, "RemoveSecretsFromEnvironment() must be present");
        idxBuilder.Should().BeGreaterThan(-1, "WebApplication.CreateBuilder(args) must be present");

        // They must appear in the required order.
        idxMark.Should().BeLessThan(idxResolve,
            "MarkProcessNotDumpable must come before ResolveAsync");
        idxResolve.Should().BeLessThan(idxRemove,
            "ResolveAsync must come before RemoveSecretsFromEnvironment");
        idxRemove.Should().BeLessThan(idxBuilder,
            "RemoveSecretsFromEnvironment must come before CreateBuilder");

        // MarkProcessNotDumpable must be the first executable statement:
        // the first line that is not blank, not a using, not a // comment, and not an attribute ([...]).
        var firstExecutableLine = source
            .Split('\n')
            .Select(l => l.TrimStart())
            .FirstOrDefault(l => l.Length > 0
                && !l.StartsWith("using ", StringComparison.Ordinal)
                && !l.StartsWith("//", StringComparison.Ordinal)
                && !l.StartsWith("[", StringComparison.Ordinal));

        firstExecutableLine.Should().NotBeNull("there must be at least one executable statement");
        firstExecutableLine!.Should().Contain("MarkProcessNotDumpable",
            "MarkProcessNotDumpable must be the first executable statement of Program.cs");
    }

    [Fact]
    public void AgentImage_SetsTheDiagnosticsSetting_InTheClaudeAndKiroStagesOnly()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "dockerfiles", "agent.Dockerfile"));

        // Split into per-stage blocks: each block starts at a "FROM " line.
        var lines = dockerfile.Split('\n');
        var stages = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentStage = "";
        var currentLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("FROM ", StringComparison.Ordinal))
            {
                if (currentStage.Length > 0)
                    stages[currentStage] = string.Join('\n', currentLines);

                // Extract "AS <name>" from "FROM ... AS <name>"
                var asIndex = line.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
                currentStage = asIndex >= 0 ? line[(asIndex + 4)..].Trim() : line;
                currentLines = [line];
            }
            else
            {
                currentLines.Add(line);
            }
        }
        if (currentStage.Length > 0)
            stages[currentStage] = string.Join('\n', currentLines);

        stages.Should().ContainKey("claude", "positive control: claude stage must exist");
        stages.Should().ContainKey("kiro", "positive control: kiro stage must exist");
        stages.Should().ContainKey("toolchain", "positive control: toolchain stage must exist");
        stages.Should().ContainKey("opencode", "positive control: opencode stage must exist");

        // claude and kiro must have the setting.
        stages["claude"].Should().Contain("ENV DOTNET_EnableDiagnostics=0",
            "the claude stage must disable .NET diagnostics");
        stages["kiro"].Should().Contain("ENV DOTNET_EnableDiagnostics=0",
            "the kiro stage must disable .NET diagnostics");

        // toolchain and opencode must NOT have it.
        stages["toolchain"].Should().NotContain("DOTNET_EnableDiagnostics",
            "the toolchain stage must not set DOTNET_EnableDiagnostics");
        stages["opencode"].Should().NotContain("DOTNET_EnableDiagnostics",
            "the opencode stage must not set DOTNET_EnableDiagnostics");
    }
}
