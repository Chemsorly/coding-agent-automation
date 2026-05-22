using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class JsonBlockExtractorTests
{
    [Fact]
    public void Extract_FencedJsonBlock_ReturnsContent()
    {
        var input = """
            Some text before
            ```json
            {"key": "value"}
            ```
            Some text after
            """;

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"key": "value"}""");
    }

    [Fact]
    public void Extract_BareFencedBlock_ReturnsContent()
    {
        var input = """
            Some text
            ```
            {"name": "test"}
            ```
            """;

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"name": "test"}""");
    }

    [Fact]
    public void Extract_FencedBlockWithArray_SkipsToFallback()
    {
        var input = """
            ```json
            [1, 2, 3]
            ```
            {"fallback": true}
            """;

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"fallback": true}""");
    }

    [Fact]
    public void Extract_BareJsonWithValidatorMatch_ReturnsCandidate()
    {
        var input = """Here is the result: {"suggestions": []} end""";

        var result = JsonBlockExtractor.Extract(input,
            c => c.Contains("suggestions", StringComparison.OrdinalIgnoreCase));

        result.Should().Be("""{"suggestions": []}""");
    }

    [Fact]
    public void Extract_BareJsonWithValidatorRejection_ContinuesSearching()
    {
        var input = """{"wrong": 1} {"suggestions": ["a"]}""";

        var result = JsonBlockExtractor.Extract(input,
            c => c.Contains("suggestions", StringComparison.OrdinalIgnoreCase));

        result.Should().Be("""{"suggestions": ["a"]}""");
    }

    [Fact]
    public void Extract_NestedBracesInStrings_HandledCorrectly()
    {
        var input = """{"text": "a { b } c", "ok": true}""";

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"text": "a { b } c", "ok": true}""");
    }

    [Fact]
    public void Extract_EscapedQuotesInStrings_HandledCorrectly()
    {
        var input = """{"text": "say \"hello\"", "ok": true}""";

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"text": "say \"hello\"", "ok": true}""");
    }

    [Fact]
    public void Extract_NoJson_ReturnsNull()
    {
        var result = JsonBlockExtractor.Extract("No JSON content here at all.");

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_EmptyString_ReturnsNull()
    {
        JsonBlockExtractor.Extract(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Extract_NullInput_ReturnsNull()
    {
        JsonBlockExtractor.Extract(null!).Should().BeNull();
    }

    [Fact]
    public void Extract_NullValidator_ReturnsFirstBalancedObject()
    {
        var input = """prefix {"first": 1} {"second": 2}""";

        var result = JsonBlockExtractor.Extract(input);

        result.Should().Be("""{"first": 1}""");
    }

    [Fact]
    public void Extract_UnbalancedBraces_ReturnsNull()
    {
        var input = """{"unclosed": true""";

        var result = JsonBlockExtractor.Extract(input);

        result.Should().BeNull();
    }

    [Fact]
    public void Extract_FencedBlockNotValidated_ReturnedWithoutValidator()
    {
        // Fenced blocks bypass the candidateValidator
        var input = """
            ```json
            {"no_match_field": true}
            ```
            """;

        var result = JsonBlockExtractor.Extract(input,
            c => c.Contains("suggestions", StringComparison.OrdinalIgnoreCase));

        result.Should().Be("""{"no_match_field": true}""");
    }
}
