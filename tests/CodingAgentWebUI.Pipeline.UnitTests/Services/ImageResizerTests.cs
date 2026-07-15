using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using NetVips;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for ImageResizer — validates downscaling logic for vision token optimization.
/// Requires libvips native library to be available (installed in CI/Docker, skipped locally if missing).
/// </summary>
[Trait("Feature", "037-issue-image-extraction")]
public class ImageResizerTests : IDisposable
{
    private readonly string _tempDir;

    public ImageResizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ImageResizerTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static bool IsVipsAvailable()
    {
        try
        {
            // Attempt a trivial NetVips operation to detect native library
            using var img = Image.Black(1, 1);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (TypeInitializationException)
        {
            return false;
        }
    }

    // ─── Within bounds — returns original bytes ─────────────────────────────────

    [Fact]
    public void DownscaleIfNeeded_ImageWithinBounds_ReturnsOriginalBytes()
    {
        if (!IsVipsAvailable()) return; // Skip when native lib unavailable

        // Create a 100x100 PNG (well within 1568 max edge)
        var filePath = CreateTestImage(100, 100);
        var originalBytes = File.ReadAllBytes(filePath);

        var result = ImageResizer.DownscaleIfNeeded(filePath);

        result.Should().BeEquivalentTo(originalBytes);
    }

    // ─── Exceeding maxEdge — resized ────────────────────────────────────────────

    [Fact]
    public void DownscaleIfNeeded_ImageExceedingMaxEdge_ReturnsResizedImage()
    {
        if (!IsVipsAvailable()) return;

        // Create a 2000x2000 PNG (exceeds default 1568 max edge)
        var filePath = CreateTestImage(2000, 2000);
        var originalBytes = File.ReadAllBytes(filePath);

        var result = ImageResizer.DownscaleIfNeeded(filePath);

        // Result should be different from original (resized)
        result.Should().NotBeEquivalentTo(originalBytes);

        // Verify output dimensions are within bounds
        using var resized = Image.NewFromBuffer(result);
        resized.Width.Should().BeLessThanOrEqualTo(1568);
        resized.Height.Should().BeLessThanOrEqualTo(1568);
    }

    // ─── Output is valid PNG ────────────────────────────────────────────────────

    [Fact]
    public void DownscaleIfNeeded_ImageExceedingMaxEdge_OutputIsValidPng()
    {
        if (!IsVipsAvailable()) return;

        var filePath = CreateTestImage(2000, 1500);

        var result = ImageResizer.DownscaleIfNeeded(filePath);

        // PNG magic bytes: 137 80 78 71 13 10 26 10
        result.Length.Should().BeGreaterThan(8);
        result[0].Should().Be(137);
        result[1].Should().Be(80);  // P
        result[2].Should().Be(78);  // N
        result[3].Should().Be(71);  // G
    }

    // ─── Custom maxEdge parameter ───────────────────────────────────────────────

    [Fact]
    public void DownscaleIfNeeded_CustomMaxEdge_RespectsParameter()
    {
        if (!IsVipsAvailable()) return;

        var filePath = CreateTestImage(500, 500);

        // 500 exceeds custom maxEdge of 200
        var result = ImageResizer.DownscaleIfNeeded(filePath, maxEdge: 200);

        using var resized = Image.NewFromBuffer(result);
        resized.Width.Should().BeLessThanOrEqualTo(200);
        resized.Height.Should().BeLessThanOrEqualTo(200);
    }

    // ─── Aspect ratio preserved ─────────────────────────────────────────────────

    [Fact]
    public void DownscaleIfNeeded_RectangularImage_PreservesAspectRatio()
    {
        if (!IsVipsAvailable()) return;

        // 3000x1500 image — longest edge is width
        var filePath = CreateTestImage(3000, 1500);

        var result = ImageResizer.DownscaleIfNeeded(filePath);

        using var resized = Image.NewFromBuffer(result);
        // Width should be capped at 1568, height scaled proportionally (~784)
        resized.Width.Should().BeLessThanOrEqualTo(1568);
        resized.Height.Should().BeLessThanOrEqualTo(1568);
        // Aspect ratio ~2:1 should be maintained (allow small rounding)
        var ratio = (double)resized.Width / resized.Height;
        ratio.Should().BeApproximately(2.0, 0.1);
    }

    // ─── Helper: create test image using NetVips ────────────────────────────────

    private string CreateTestImage(int width, int height)
    {
        var filePath = Path.Combine(_tempDir, $"test-{width}x{height}-{Guid.NewGuid():N}.png");
        using var image = Image.Black(width, height, bands: 3);
        image.WriteToFile(filePath);
        return filePath;
    }
}
