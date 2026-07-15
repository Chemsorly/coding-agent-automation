namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a downloaded image file on the local filesystem.
/// Not serialized — lives in PipelineStepContext only.
/// </summary>
public sealed record DownloadedImage
{
    public required string LocalPath { get; init; }

    public required string LocalFilename { get; init; }

    public required ImageReference Reference { get; init; }

    public required long FileSizeBytes { get; init; }

    public required string MimeType { get; init; }
}
