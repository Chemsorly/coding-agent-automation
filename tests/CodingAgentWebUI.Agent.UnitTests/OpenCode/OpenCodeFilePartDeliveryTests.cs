using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests that OpenCodeAgentProvider.ExecuteAsync sends file parts for images
/// when request.ImagePaths is populated.
/// Task 9.3 — OpenCode native file part delivery.
/// </summary>
[Trait("Feature", "issue-image-extraction")]
public class OpenCodeFilePartDeliveryTests
{
    /// <summary>
    /// When ImagePaths has entries, the message request should contain file parts
    /// in addition to the text part.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithImagePaths_SendsFilePartsInRequest()
    {
        // Arrange — create a temp PNG file (minimal 1x1 PNG)
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var pngPath = Path.Combine(tempDir, "test-image.png");
        File.WriteAllBytes(pngPath, CreateMinimalPng());

        try
        {
            var ctx = OpenCodeTestHelpers.CreateTestContext();
            OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
            ctx.Handler.ForUrlPattern("/session/.+/message",
                new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

            var request = new AgentRequest
            {
                Prompt = "Fix the bug",
                WorkspacePath = tempDir,
                Timeout = TimeSpan.FromMinutes(5),
                ImagePaths = [pngPath]
            };

            // Act
            var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

            // Assert
            Assert.Equal(ExitCodes.Success, result.ExitCode);

            var messageReq = ctx.Handler.Requests
                .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
            Assert.NotNull(messageReq);
            Assert.NotNull(messageReq.Body);

            var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body);
            var parts = body.GetProperty("parts");

            // Should have 2 parts: text + file
            Assert.Equal(2, parts.GetArrayLength());

            // First part is text
            var textPart = parts[0];
            Assert.Equal("text", textPart.GetProperty("type").GetString());
            Assert.Equal("Fix the bug", textPart.GetProperty("text").GetString());

            // Second part is file
            var filePart = parts[1];
            Assert.Equal("file", filePart.GetProperty("type").GetString());
            Assert.Equal("image/png", filePart.GetProperty("mime").GetString());
            Assert.Equal("test-image.png", filePart.GetProperty("filename").GetString());

            var url = filePart.GetProperty("url").GetString();
            Assert.NotNull(url);
            Assert.StartsWith("data:image/png;base64,", url);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// When ImagePaths is null, only the text part is sent (backward compat).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNullImagePaths_SendsOnlyTextPart()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "Hello");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var messageReq = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageReq);

        var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body!);
        var parts = body.GetProperty("parts");

        // Only 1 text part
        Assert.Equal(1, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
    }

    /// <summary>
    /// When ImagePaths is empty list, only the text part is sent.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyImagePaths_SendsOnlyTextPart()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var request = new AgentRequest
        {
            Prompt = "Hello",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromMinutes(5),
            ImagePaths = []
        };

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var messageReq = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageReq);

        var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body!);
        var parts = body.GetProperty("parts");

        Assert.Equal(1, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
    }

    /// <summary>
    /// JPEG extension produces correct mime type.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithJpegImage_ProducesCorrectMimeType()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var jpgPath = Path.Combine(tempDir, "photo.jpg");
        File.WriteAllBytes(jpgPath, CreateMinimalJpeg());

        try
        {
            var ctx = OpenCodeTestHelpers.CreateTestContext();
            OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
            ctx.Handler.ForUrlPattern("/session/.+/message",
                new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

            var request = new AgentRequest
            {
                Prompt = "Check this",
                WorkspacePath = tempDir,
                Timeout = TimeSpan.FromMinutes(5),
                ImagePaths = [jpgPath]
            };

            // Act
            var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

            // Assert
            Assert.Equal(ExitCodes.Success, result.ExitCode);

            var messageReq = ctx.Handler.Requests
                .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
            Assert.NotNull(messageReq);

            var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body!);
            var filePart = body.GetProperty("parts")[1];
            Assert.Equal("image/jpeg", filePart.GetProperty("mime").GetString());
            Assert.StartsWith("data:image/jpeg;base64,", filePart.GetProperty("url").GetString());
            Assert.Equal("photo.jpg", filePart.GetProperty("filename").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// When an image file doesn't exist, it should be skipped gracefully (not throw).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithMissingImageFile_SkipsGracefully()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var request = new AgentRequest
        {
            Prompt = "Fix it",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromMinutes(5),
            ImagePaths = ["/nonexistent/path/image.png"]
        };

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert — should succeed, just without file parts
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var messageReq = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageReq);

        var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body!);
        var parts = body.GetProperty("parts");

        // Only text part (file was skipped)
        Assert.Equal(1, parts.GetArrayLength());
    }

    /// <summary>
    /// Multiple images produce multiple file parts in order.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithMultipleImages_SendsMultipleFileParts()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var png1 = Path.Combine(tempDir, "screenshot1.png");
        var png2 = Path.Combine(tempDir, "screenshot2.png");
        File.WriteAllBytes(png1, CreateMinimalPng());
        File.WriteAllBytes(png2, CreateMinimalPng());

        try
        {
            var ctx = OpenCodeTestHelpers.CreateTestContext();
            OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
            ctx.Handler.ForUrlPattern("/session/.+/message",
                new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

            var request = new AgentRequest
            {
                Prompt = "Review",
                WorkspacePath = tempDir,
                Timeout = TimeSpan.FromMinutes(5),
                ImagePaths = [png1, png2]
            };

            // Act
            var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

            // Assert
            Assert.Equal(ExitCodes.Success, result.ExitCode);

            var messageReq = ctx.Handler.Requests
                .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
            Assert.NotNull(messageReq);

            var body = JsonSerializer.Deserialize<JsonElement>(messageReq.Body!);
            var parts = body.GetProperty("parts");

            // 1 text + 2 file parts
            Assert.Equal(3, parts.GetArrayLength());
            Assert.Equal("text", parts[0].GetProperty("type").GetString());
            Assert.Equal("file", parts[1].GetProperty("type").GetString());
            Assert.Equal("screenshot1.png", parts[1].GetProperty("filename").GetString());
            Assert.Equal("file", parts[2].GetProperty("type").GetString());
            Assert.Equal("screenshot2.png", parts[2].GetProperty("filename").GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a minimal valid 1x1 PNG file (67 bytes).</summary>
    private static byte[] CreateMinimalPng()
    {
        // Minimal 1x1 white PNG
        return
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, // 8-bit RGB
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, // IDAT chunk
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00, // compressed data
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC, // ...
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, // IEND chunk
            0x44, 0xAE, 0x42, 0x60, 0x82                      // IEND CRC
        ];
    }

    /// <summary>Creates a minimal valid JPEG file.</summary>
    private static byte[] CreateMinimalJpeg()
    {
        // Minimal JPEG: SOI + APP0 (JFIF) + minimal data + EOI
        return
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, // SOI + APP0 header
            0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, // JFIF
            0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9                // EOI
        ];
    }
}
