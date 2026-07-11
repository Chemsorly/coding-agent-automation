using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Unit tests for MessagePart serialization with the new Mime, Url, and Filename fields.
/// Verifies WhenWritingNull behavior omits null fields and includes non-null fields.
/// </summary>
[Trait("Feature", "issue-image-extraction")]
public class MessagePartSerializationTests
{
    [Fact]
    public void TextOnlyPart_OmitsNullImageFields()
    {
        // Arrange
        var part = new MessagePart { Type = "text", Text = "hello" };

        // Act
        var json = JsonSerializer.Serialize(part, OpenCodeJson.JsonOptions);

        // Assert — null fields (Mime, Url, Filename) should not appear
        Assert.DoesNotContain("mime", json);
        Assert.DoesNotContain("url", json);
        Assert.DoesNotContain("filename", json);
        Assert.Contains("\"type\":\"text\"", json);
        Assert.Contains("\"text\":\"hello\"", json);
    }

    [Fact]
    public void FilePart_IncludesAllImageFields()
    {
        // Arrange
        var part = new MessagePart
        {
            Type = "file",
            Mime = "image/png",
            Url = "data:image/png;base64,abc123",
            Filename = "issue-42-image-001.png"
        };

        // Act
        var json = JsonSerializer.Serialize(part, OpenCodeJson.JsonOptions);

        // Assert — all specified fields should be present
        Assert.Contains("\"type\":\"file\"", json);
        Assert.Contains("\"mime\":\"image/png\"", json);
        Assert.Contains("\"url\":\"data:image/png;base64,abc123\"", json);
        Assert.Contains("\"filename\":\"issue-42-image-001.png\"", json);
        // Text should be omitted since it's null
        Assert.DoesNotContain("\"text\"", json);
    }

    [Fact]
    public void FilePart_Deserializes_AllFields()
    {
        // Arrange
        var json = """{"type":"file","mime":"image/png","url":"data:image/png;base64,abc","filename":"test.png"}""";

        // Act
        var part = JsonSerializer.Deserialize<MessagePart>(json, OpenCodeJson.JsonOptions);

        // Assert
        Assert.NotNull(part);
        Assert.Equal("file", part.Type);
        Assert.Equal("image/png", part.Mime);
        Assert.Equal("data:image/png;base64,abc", part.Url);
        Assert.Equal("test.png", part.Filename);
        Assert.Null(part.Text);
    }
}
