using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for CiLogWriter.
/// Feature: provider-interface-gaps
/// </summary>
public class CiLogWriterPropertyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private string CreateTempWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cilogwriter-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// Feature: provider-interface-gaps, Property 6: CiLogWriter round-trip
    /// For any PipelineRunStatus with failed jobs and non-null LogContent, WriteJobLogs returns
    /// a dictionary where: (a) every failed job with LogContent has an entry keyed by its JobId,
    /// (b) reading the file at each mapped path yields content equal to the original LogContent,
    /// and (c) the dictionary contains no entries for jobs without LogContent.
    /// **Validates: REQ-4.2**
    /// </summary>
    // Feature: provider-interface-gaps, Property 6: CiLogWriter round-trip
    [Property(MaxTest = 20, Arbitrary = [typeof(PipelineRunStatusArbitrary)])]
    public void WriteJobLogs_RoundTrips_LogContent_For_Failed_Jobs(PipelineRunStatusWithLogs input)
    {
        // Arrange
        var mockLogger = new Mock<Serilog.ILogger>();
        var writer = new CiLogWriter(mockLogger.Object);
        var workspacePath = CreateTempWorkspace();
        var runId = $"run-{Guid.NewGuid():N}";

        // Act
        var result = writer.WriteJobLogs(input.Status, workspacePath, runId);

        // Identify which jobs should have entries: failed with non-null/non-empty LogContent
        var expectedJobs = input.Status.Jobs
            .Where(j => j.State == PipelineRunState.Failed && !string.IsNullOrEmpty(j.LogContent))
            .ToList();

        // (a) Every failed job with LogContent has an entry keyed by its JobId
        foreach (var job in expectedJobs)
        {
            Assert.True(result.ContainsKey(job.JobId),
                $"Expected dictionary to contain entry for JobId {job.JobId} ('{job.Name}')");
        }

        // (b) Reading the file at each mapped path yields content equal to the original LogContent
        foreach (var job in expectedJobs)
        {
            var relativePath = result[job.JobId];
            var fullPath = Path.Combine(workspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath),
                $"Expected file to exist at {fullPath} for JobId {job.JobId}");

            var fileContent = File.ReadAllText(fullPath);
            Assert.Equal(job.LogContent, fileContent);
        }

        // (c) No entries exist for jobs without LogContent
        var jobsWithoutContent = input.Status.Jobs
            .Where(j => j.State != PipelineRunState.Failed || string.IsNullOrEmpty(j.LogContent))
            .Select(j => j.JobId)
            .ToHashSet();

        foreach (var kvp in result)
        {
            Assert.False(jobsWithoutContent.Contains(kvp.Key),
                $"Dictionary should not contain entry for JobId {kvp.Key} (no LogContent or not failed)");
        }

        // Also verify the count matches exactly
        Assert.Equal(expectedJobs.Count, result.Count);
    }
}

/// <summary>
/// Wrapper type for PipelineRunStatus instances used in property-based testing.
/// Contains a mix of failed jobs with/without LogContent and non-failed jobs.
/// </summary>
public sealed class PipelineRunStatusWithLogs
{
    public PipelineRunStatus Status { get; }

    public PipelineRunStatusWithLogs(PipelineRunStatus status) => Status = status;

    public override string ToString()
    {
        var failed = Status.Jobs.Count(j => j.State == PipelineRunState.Failed);
        var withLogs = Status.Jobs.Count(j => j.State == PipelineRunState.Failed && !string.IsNullOrEmpty(j.LogContent));
        return $"PipelineRunStatus(Jobs={Status.Jobs.Count}, Failed={failed}, WithLogs={withLogs})";
    }
}

/// <summary>
/// FsCheck arbitrary that generates PipelineRunStatus instances with a mix of:
/// - Failed jobs with non-null LogContent (should produce dictionary entries)
/// - Failed jobs with null LogContent (should NOT produce entries)
/// - Non-failed jobs with or without LogContent (should NOT produce entries)
/// Uses unique JobIds to avoid collisions.
/// </summary>
public static class PipelineRunStatusArbitrary
{
    /// <summary>
    /// Characters safe for file names and log content — avoids null bytes and
    /// other characters that could cause file I/O issues.
    /// </summary>
    private static readonly char[] SafeChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()-_=+[]{}|;:',.<>?/\n\r\t"
            .ToCharArray();

    /// <summary>
    /// Characters safe for job names — excludes path-invalid characters.
    /// </summary>
    private static readonly char[] NameChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_ ".ToCharArray();

    public static Arbitrary<PipelineRunStatusWithLogs> PipelineRunStatusWithLogss()
    {
        var gen =
            from jobCount in Gen.Choose(1, 8)
            from jobs in Gen.ArrayOf(JobGen(), jobCount)
            let uniqueJobs = AssignUniqueJobIds(jobs)
            select new PipelineRunStatusWithLogs(new PipelineRunStatus
            {
                State = uniqueJobs.Any(j => j.State == PipelineRunState.Failed)
                    ? PipelineRunState.Failed
                    : PipelineRunState.Passed,
                Jobs = uniqueJobs
            });

        return gen.ToArbitrary();
    }

    private static IReadOnlyList<PipelineJobResult> AssignUniqueJobIds(PipelineJobResult[] jobs)
    {
        // Assign sequential unique IDs to avoid collisions
        return jobs.Select((j, i) => new PipelineJobResult
        {
            Name = j.Name,
            State = j.State,
            FailureReason = j.FailureReason,
            LogUrl = j.LogUrl,
            JobId = i + 1,
            LogContent = j.LogContent
        }).ToList();
    }

    private static Gen<PipelineJobResult> JobGen()
    {
        return Gen.OneOf(
            FailedJobWithLogsGen(),
            FailedJobWithoutLogsGen(),
            NonFailedJobGen()
        );
    }

    private static Gen<PipelineJobResult> FailedJobWithLogsGen()
    {
        return
            from name in SafeNameGen()
            from content in LogContentGen()
            select new PipelineJobResult
            {
                Name = name,
                State = PipelineRunState.Failed,
                FailureReason = "Test failure",
                LogContent = content,
                JobId = 0 // Will be reassigned
            };
    }

    private static Gen<PipelineJobResult> FailedJobWithoutLogsGen()
    {
        return
            from name in SafeNameGen()
            select new PipelineJobResult
            {
                Name = name,
                State = PipelineRunState.Failed,
                FailureReason = "Test failure",
                LogContent = null,
                JobId = 0 // Will be reassigned
            };
    }

    private static Gen<PipelineJobResult> NonFailedJobGen()
    {
        return
            from name in SafeNameGen()
            from state in Gen.Elements(PipelineRunState.Passed, PipelineRunState.Running, PipelineRunState.Pending)
            select new PipelineJobResult
            {
                Name = name,
                State = state,
                JobId = 0 // Will be reassigned
            };
    }

    private static Gen<string> SafeNameGen()
    {
        return
            from len in Gen.Choose(3, 30)
            from chars in Gen.ArrayOf(Gen.Elements(NameChars), len)
            select new string(chars);
    }

    private static Gen<string> LogContentGen()
    {
        return
            from len in Gen.Choose(10, 500)
            from chars in Gen.ArrayOf(Gen.Elements(SafeChars), len)
            select new string(chars);
    }
}
