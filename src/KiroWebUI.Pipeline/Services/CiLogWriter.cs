using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Writes CI job log content to workspace files and returns a mapping of jobId → filePath.
/// Extracted from PipelineOrchestrationService.WriteCiLogsToWorkspace.
/// </summary>
public class CiLogWriter
{
    private readonly Serilog.ILogger _logger;

    public CiLogWriter(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Writes log content for failed jobs to .kiro/quality-gates/ and returns
    /// a dictionary mapping jobId to the workspace-relative file path.
    /// </summary>
    public IReadOnlyDictionary<long, string> WriteJobLogs(
        PipelineRunStatus ciStatus, string workspacePath, string runId)
    {
        ArgumentNullException.ThrowIfNull(ciStatus);
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(runId);

        var result = new Dictionary<long, string>();

        var failedJobs = ciStatus.Jobs
            .Where(j => j.State == PipelineRunState.Failed && !string.IsNullOrEmpty(j.LogContent))
            .ToList();

        if (failedJobs.Count == 0)
            return result;

        var logsDir = Path.Combine(workspacePath, PromptBuilder.QualityGatesOutputDirectory);
        try
        {
            Directory.CreateDirectory(logsDir);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to prepare quality gates output directory {LogsDir}", logsDir);
            return result;
        }

        var written = 0;
        foreach (var job in failedJobs)
        {
            try
            {
                var safeJobName = string.Join("_", job.Name.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeJobName))
                    safeJobName = $"job_{job.JobId}";

                var fileName = $"ci-{safeJobName}_{job.JobId}.log";
                var fullPath = Path.Combine(logsDir, fileName);

                File.WriteAllText(fullPath, job.LogContent);

                // Store the workspace-relative path
                var relativePath = Path.Combine(PromptBuilder.QualityGatesOutputDirectory, fileName).Replace('\\', '/');
                result[job.JobId] = relativePath;

                _logger.Debug("Wrote CI log for job '{JobName}' to {LogPath}", job.Name, relativePath);
                written++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to write CI log for job '{JobName}'", job.Name);
            }
        }

        if (written > 0)
            _logger.Information("Wrote {Count} CI log file(s) to {LogsDir}", written, logsDir);

        return result;
    }
}
