using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for ImageDownloadService — validates download logic, SSRF,
/// budgets, content validation, redirect handling, and GitLab support.
/// </summary>
[Trait("Feature", "037-issue-image-extraction")]
public class ImageDownloadServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ImageDownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"img-dl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static PipelineConfiguration DefaultConfig() => new()
    {
        EnableIssueImageExtraction = true,
        MaxIssueImages = 10,
        MaxImageSizeBytes = 5_242_880,
        MaxTotalImageSizeBytes = 20_971_520,
        TotalImageDownloadTimeoutSeconds = 60,
        ImageDownloadTimeoutSeconds = 30
    };

    private static ImageReference MakeRef(string url) => new()
    {
        Url = url,
        AltText = "test",
        SourceType = ImageSourceType.Body,
        SourceIndex = 0
    };

    private static byte[] PngMagicBytes => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static byte[] JpegMagicBytes => [0xFF, 0xD8, 0xFF, 0xE0];
    private static byte[] GifMagicBytes => [0x47, 0x49, 0x46, 0x38, 0x39, 0x61];

    // ─── Config disabled → empty result ─────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_ConfigDisabled_ReturnsEmpty()
    {
        var config = DefaultConfig() with { EnableIssueImageExtraction = false };
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://github.com/img.png")],
            _tempDir, "token", null, null, config, CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Scheme validation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://example.com/image.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/image.png")]
    public async Task DownloadAllAsync_NonHttpsScheme_SkipsImage(string url)
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef(url)],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Successful download ────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_ValidPng_ReturnsDownloadedImage()
    {
        var pngContent = CreateMinimalPng();
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://github.com/user-attachments/assets/abc123/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].MimeType.Should().Be("image/png");
        result[0].FileSizeBytes.Should().Be(pngContent.Length);
        File.Exists(result[0].LocalPath).Should().BeTrue();
    }

    // ─── Content-Type validation ────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_InvalidContentType_SkipsImage()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent([0x00, 0x01, 0x02]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Magic bytes mismatch ───────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_MagicBytesMismatch_SkipsImage()
    {
        // Claims PNG content-type but has JPEG magic bytes
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(JpegMagicBytes);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Extension vs Content-Type mismatch ─────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_ExtensionContentTypeMismatch_SkipsImage()
    {
        var pngContent = CreateMinimalPng();
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            // URL says .gif but Content-Type says image/png
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/image.gif")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Per-image size budget exceeded ─────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_PerImageSizeExceeded_SkipsButContinuesOthers()
    {
        var smallPng = CreateMinimalPng();
        var bigContent = new byte[1024]; // "big" content
        Array.Copy(PngMagicBytes, bigContent, PngMagicBytes.Length);

        var callCount = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (Interlocked.Increment(ref callCount) == 1)
            {
                // First image: exceeds budget
                response.Content = new ByteArrayContent(bigContent);
            }
            else
            {
                // Second image: within budget
                response.Content = new ByteArrayContent(smallPng);
            }
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        var config = DefaultConfig() with { MaxImageSizeBytes = 800 };
        using var service = new ImageDownloadService(handler);

        var images = new[]
        {
            MakeRef("https://example.com/big.png"),
            MakeRef("https://example.com/small.png")
        };
        var result = await service.DownloadAllAsync(
            images, _tempDir, "token", null, null, config, CancellationToken.None);

        // Big one skipped, small one downloaded
        result.Should().HaveCount(1);
    }

    // ─── Total byte budget exceeded ─────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_TotalBudgetExceeded_StopsRemaining()
    {
        var pngContent = CreateMinimalPng(); // ~67 bytes
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        // Total budget < size of 2 images
        var config = DefaultConfig() with { MaxTotalImageSizeBytes = 50 };
        using var service = new ImageDownloadService(handler);

        var images = new[]
        {
            MakeRef("https://example.com/a.png"),
            MakeRef("https://example.com/b.png")
        };
        var result = await service.DownloadAllAsync(
            images, _tempDir, "token", null, null, config, CancellationToken.None);

        // At most 1 should succeed (first hits budget on second)
        result.Count.Should().BeLessThanOrEqualTo(1);
    }

    // ─── Redirect handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_CrossOriginRedirect_StripsAuth()
    {
        string? authHeaderOnRedirect = null;
        var hopCount = 0;
        var pngContent = CreateMinimalPng();

        var handler = new MockHttpMessageHandler(request =>
        {
            hopCount++;
            if (hopCount == 1)
            {
                // First request to github.com -> redirect to cdn.example.com
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://cdn.example.com/image.png");
                return response;
            }
            // Second request: capture auth header
            authHeaderOnRedirect = request.Headers.Authorization?.ToString();
            var finalResponse = new HttpResponseMessage(HttpStatusCode.OK);
            finalResponse.Content = new ByteArrayContent(pngContent);
            finalResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return finalResponse;
        });
        using var service = new ImageDownloadService(handler);

        await service.DownloadAllAsync(
            [MakeRef("https://github.com/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        // Auth should be stripped on cross-origin redirect
        authHeaderOnRedirect.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAllAsync_MaxRedirectsExceeded_SkipsImage()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.com/next");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── SSRF blocked ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_SsrfBlocked_SkipsImage()
    {
        // DNS resolves to private IP
        Func<string, CancellationToken, Task<IPAddress[]>> dnsResolver =
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
        using var service = new ImageDownloadService(dnsResolver: dnsResolver);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://evil.internal/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Extensionless URL with Content-Type ────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_ExtensionlessUrl_UsesContentTypeExtension()
    {
        var pngContent = CreateMinimalPng();
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://github.com/user-attachments/assets/abc-def-123")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].LocalFilename.Should().EndWith(".png");
    }

    // ─── GitLab relative URL ────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_GitLabRelativeUrl_ConstructsApiPath()
    {
        Uri? requestedUri = null;
        string? privateTokenHeader = null;
        var pngContent = CreateMinimalPng();

        var handler = new MockHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            privateTokenHeader = request.Headers.TryGetValues("PRIVATE-TOKEN", out var vals)
                ? vals.FirstOrDefault() : null;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        await service.DownloadAllAsync(
            [MakeRef("/uploads/abc123secret/screenshot.png")],
            _tempDir, "my-token", "https://gitlab.example.com/api/v4",
            "42", DefaultConfig(), CancellationToken.None);

        requestedUri.Should().NotBeNull();
        requestedUri!.ToString().Should().Contain("/projects/42/uploads/abc123secret/screenshot.png");
        privateTokenHeader.Should().Be("my-token");
    }

    // ─── MaxIssueImages config limit ────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_ExceedsMaxIssueImages_TakesOnlyConfigured()
    {
        var pngContent = CreateMinimalPng();
        var downloadCount = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref downloadCount);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        var config = DefaultConfig() with { MaxIssueImages = 2 };
        using var service = new ImageDownloadService(handler);

        var images = Enumerable.Range(0, 5)
            .Select(i => MakeRef($"https://example.com/img{i}.png"))
            .ToList();

        await service.DownloadAllAsync(
            images, _tempDir, "token", null, null, config, CancellationToken.None);

        downloadCount.Should().BeLessThanOrEqualTo(2);
    }

    // ─── Auth header for GitHub hosts ───────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_GitHubHost_SendsBearerAuth()
    {
        string? authHeader = null;
        var pngContent = CreateMinimalPng();

        var handler = new MockHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        await service.DownloadAllAsync(
            [MakeRef("https://user-images.githubusercontent.com/123/img.png")],
            _tempDir, "my-token", null, null, DefaultConfig(), CancellationToken.None);

        authHeader.Should().Be("Bearer my-token");
    }

    [Fact]
    public async Task DownloadAllAsync_NonGitHubHost_NoAuthHeader()
    {
        string? authHeader = null;
        var pngContent = CreateMinimalPng();

        var handler = new MockHttpMessageHandler(request =>
        {
            authHeader = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new ByteArrayContent(pngContent);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        await service.DownloadAllAsync(
            [MakeRef("https://random-cdn.example.com/img.png")],
            _tempDir, "my-token", null, null, DefaultConfig(), CancellationToken.None);

        authHeader.Should().BeNull();
    }

    // ─── ValidateMagicBytes static method ───────────────────────────────────────

    [Fact]
    public void ValidateMagicBytes_ValidPng_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "test.png");
        File.WriteAllBytes(path, PngMagicBytes);

        ImageDownloadService.ValidateMagicBytes(path, "image/png").Should().BeTrue();
    }

    [Fact]
    public void ValidateMagicBytes_ValidJpeg_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "test.jpg");
        File.WriteAllBytes(path, JpegMagicBytes);

        ImageDownloadService.ValidateMagicBytes(path, "image/jpeg").Should().BeTrue();
    }

    [Fact]
    public void ValidateMagicBytes_ValidGif_ReturnsTrue()
    {
        var path = Path.Combine(_tempDir, "test.gif");
        File.WriteAllBytes(path, GifMagicBytes);

        ImageDownloadService.ValidateMagicBytes(path, "image/gif").Should().BeTrue();
    }

    [Fact]
    public void ValidateMagicBytes_Mismatch_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "test.png");
        File.WriteAllBytes(path, JpegMagicBytes); // JPEG bytes but claims PNG

        ImageDownloadService.ValidateMagicBytes(path, "image/png").Should().BeFalse();
    }

    [Fact]
    public void ValidateMagicBytes_FileTooSmall_ReturnsFalse()
    {
        var path = Path.Combine(_tempDir, "tiny");
        File.WriteAllBytes(path, [0x00, 0x01]);

        ImageDownloadService.ValidateMagicBytes(path, "image/png").Should().BeFalse();
    }

    // ─── Redirect to non-HTTPS ──────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_RedirectToHttp_SkipsImage()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://insecure.example.com/image.png");
            return response;
        });
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/image.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── HTTP error status ──────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_Http404_SkipsImage()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [MakeRef("https://example.com/missing.png")],
            _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Empty image list ───────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAllAsync_EmptyList_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));
        using var service = new ImageDownloadService(handler);

        var result = await service.DownloadAllAsync(
            [], _tempDir, "token", null, null, DefaultConfig(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal valid 1x1 PNG (cannot pass dimension validation of ≥32px).
    /// Use CreateValidPng for dimension-passing tests.
    /// </summary>
    private static byte[] CreateMinimalPng()
    {
        // Minimal 64x64 PNG (valid magic bytes + IHDR with 64x64 dimensions)
        // This is a synthetic valid PNG that NetVips can read
        using var ms = new MemoryStream();
        // PNG Signature
        ms.Write(PngMagicBytes);
        // IHDR chunk: length=13, type=IHDR, width=64, height=64, bit depth=8, color=2(RGB)
        WriteChunk(ms, "IHDR", [
            0x00, 0x00, 0x00, 0x40, // width = 64
            0x00, 0x00, 0x00, 0x40, // height = 64
            0x08,                    // bit depth = 8
            0x02,                    // color type = RGB
            0x00,                    // compression = deflate
            0x00,                    // filter = adaptive
            0x00                     // interlace = none
        ]);
        // IDAT chunk with minimal valid compressed data (empty image)
        // zlib header (78 01) + deflate block for 64 rows of (filter_byte + 192 bytes RGB)
        var idatData = CreateMinimalIdatData(64, 64);
        WriteChunk(ms, "IDAT", idatData);
        // IEND chunk
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreateMinimalIdatData(int width, int height)
    {
        // Create minimal valid zlib-compressed PNG image data
        var rowBytes = 1 + (width * 3); // filter byte + RGB
        var raw = new byte[rowBytes * height];
        // All zeros = black image with "None" filter (0x00) per row
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(
            compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        // Wrap in zlib: header (78 01) + deflate data + adler32
        var deflateBytes = compressed.ToArray();
        using var result = new MemoryStream();
        result.WriteByte(0x78); // CMF
        result.WriteByte(0x01); // FLG
        result.Write(deflateBytes);
        // Adler32 of uncompressed data
        var adler = ComputeAdler32(raw);
        result.WriteByte((byte)(adler >> 24));
        result.WriteByte((byte)(adler >> 16));
        result.WriteByte((byte)(adler >> 8));
        result.WriteByte((byte)adler);
        return result.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        // Length (4 bytes big-endian)
        var len = BitConverter.GetBytes(data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);
        s.Write(len);
        // Type (4 ASCII bytes)
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        // Data
        s.Write(data);
        // CRC32 of type + data
        var crcInput = new byte[4 + data.Length];
        Array.Copy(typeBytes, crcInput, 4);
        Array.Copy(data, 0, crcInput, 4, data.Length);
        var crc = ComputeCrc32(crcInput);
        var crcBytes = BitConverter.GetBytes(crc);
        if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
        s.Write(crcBytes);
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static uint ComputeAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var byte_ in data)
        {
            a = (a + byte_) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}

/// <summary>
/// Simple mock HTTP handler for testing ImageDownloadService.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}
