using System.Text;

namespace CodingAgentWebUI.Pipeline.Persistence;

/// <summary>
/// Provides atomic file write operations using the write-to-temp + fsync + rename pattern.
/// Guarantees file integrity on process crash — the target file is either the old content
/// or the new content, never a partial write.
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Atomically writes content to the target path.
    /// Pattern: write to GUID-based temp file → flush to disk → atomic rename.
    /// </summary>
    /// <param name="targetPath">Final destination path for the file.</param>
    /// <param name="content">UTF-8 string content to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteAsync(string targetPath, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);

        var tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            // Write content directly via FileStream so we can flush before rename
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await fs.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true); // OS-level fsync — ensures data is on physical media
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of temp file on failure.
            // If rename fails, original file stays intact (we throw, don't delete original).
            try { File.Delete(tmpPath); } catch { /* ignored */ }
            throw;
        }
    }
}
