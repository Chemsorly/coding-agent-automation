using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Reads brain skill files from a cloned brain directory and assembles the skills text for injection.
/// Spec 054, req 5 (skills injection).
/// </summary>
public static class BrainSkillsReader
{
    /// <summary>
    /// Reads the brain skill files from <paramref name="brainPath"/>, assembles the injection text,
    /// and reads the HEAD commit SHA. Never throws (T6).
    /// </summary>
    public static async Task<BrainSkillsInjection> ReadAsync(
        string brainPath,
        string? repositoryShortName,
        IReadOnlyList<string> stackLabels,
        IGitWorkspaceProvider brainProvider,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var candidatePaths = BuildCandidatePaths(repositoryShortName, stackLabels);
            var files = await ReadFilesAsync(brainPath, candidatePaths, logger, ct);

            var assembly = BrainSkillsAssembler.Assemble(files, repositoryShortName, stackLabels);

            string? commitSha = null;
            try
            {
                commitSha = await brainProvider.GetHeadCommitShaAsync(brainPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(ex, "Brain skills reader could not read brain commit SHA: {Error}", ex.Message);
            }

            if (assembly.FilesSkipped.Count > 0)
            {
                foreach (var skip in assembly.FilesSkipped)
                    logger.Warning("Brain skill file {SkillFile} skipped: {ValidationReason}", skip.Path, skip.Reason);
            }

            return new BrainSkillsInjection(
                assembly.Text, assembly.Result, assembly.Bytes,
                assembly.FilesInjected, commitSha);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Brain skills reader failed, returning no-brain result: {Error}", ex.Message);
            return new BrainSkillsInjection(string.Empty, BrainSkillsResults.NoBrain, 0, [], null);
        }
    }

    private static List<string> BuildCandidatePaths(string? repositoryShortName, IReadOnlyList<string> stackLabels)
    {
        var paths = new List<string> { BrainLayout.GeneralSkillPath };
        if (!string.IsNullOrEmpty(repositoryShortName))
            paths.Add(BrainLayout.RepositorySkillPath(repositoryShortName));
        foreach (var label in stackLabels)
        {
            var techPath = BrainLayout.TechnologySkillPath(label);
            if (techPath is not null) paths.Add(techPath);
        }
        return paths;
    }

    private static async Task<Dictionary<string, string>> ReadFilesAsync(
        string brainPath,
        List<string> candidatePaths,
        ILogger logger,
        CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in candidatePaths)
        {
            var full = Path.Combine(brainPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            try
            {
                // T22: read with UTF-8 without BOM, throwOnInvalidBytes
                var content = await File.ReadAllTextAsync(full, new System.Text.UTF8Encoding(false, true), ct);
                if (new FileInfo(full).Length > BrainLayout.MaxSkillFileBytes)
                {
                    logger.Warning("Brain skill file {SkillFile} skipped: over_budget (file too large)", rel);
                    // Pass an intentionally invalid content so the assembler skips it
                    files[rel] = string.Empty;
                }
                else
                {
                    files[rel] = content;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(ex, "Brain skill file {SkillFile} could not be read: {Error}", rel, ex.Message);
            }
        }
        return files;
    }
}
