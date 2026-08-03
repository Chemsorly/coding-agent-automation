using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Groups the per-image parameters passed to the private
/// <c>DownloadSingleAsync</c> method in <see cref="ImageDownloadService"/>
/// to reduce parameter count (S107).
/// </summary>
internal sealed record ImageDownloadContext(
    ImageReference ImageRef,
    string TargetDirectory,
    string AuthToken,
    string? GitlabApiUrl,
    string? GitlabProjectId,
    PipelineConfiguration Config,
    ByteBudget Budget);

/// <summary>
/// Thread-safe byte counter for tracking total download budget across concurrent tasks.
/// </summary>
internal sealed class ByteBudget
{
    private long _totalBytes;

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public void Add(long bytes) => Interlocked.Add(ref _totalBytes, bytes);

    /// <summary>
    /// Atomically reserves bytes if doing so doesn't exceed the limit.
    /// Returns true if reservation succeeded.
    /// </summary>
    public bool TryReserve(long bytes, long maxTotal)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _totalBytes);
            if (current + bytes > maxTotal)
                return false;
            if (Interlocked.CompareExchange(ref _totalBytes, current + bytes, current) == current)
                return true;
        }
    }
}
