using NetVips;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Downscales images exceeding a maximum edge size to avoid wasting vision tokens
/// on resolution the model cannot use. Uses NetVips for efficient JPEG shrink-on-load.
/// </summary>
public static class ImageResizer
{
    /// <summary>
    /// Returns the image bytes, downscaled to fit within <paramref name="maxEdge"/> pixels
    /// on the longest edge. Returns original file bytes if already within bounds.
    /// Output is always PNG for consistency.
    /// </summary>
    /// <param name="filePath">Path to the image file on disk.</param>
    /// <param name="maxEdge">Maximum allowed pixels on the longest edge (default: 1568, Claude's max tile size).</param>
    /// <returns>PNG bytes of the (possibly resized) image.</returns>
    public static byte[] DownscaleIfNeeded(string filePath, int maxEdge = 1568)
    {
        using var image = Image.NewFromFile(filePath, access: Enums.Access.Sequential);
        if (image.Width <= maxEdge && image.Height <= maxEdge)
            return File.ReadAllBytes(filePath);

        // Image.Thumbnail exploits JPEG shrink-on-load (4x faster for large JPEGs)
        using var thumb = Image.Thumbnail(filePath, maxEdge, height: maxEdge);
        return thumb.WriteToBuffer(".png");
    }
}
