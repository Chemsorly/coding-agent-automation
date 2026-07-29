using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog.Events;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Telemetry;

public class LogLevelParserTests
{
    [Theory]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("dbg", LogEventLevel.Debug)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("DEBUG", LogEventLevel.Debug)]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("err", LogEventLevel.Error)]
    public void Parse_ShouldReturnMappedLevel_WhenValueMatchesKnownAlias(string value, LogEventLevel expected)
    {
        var result = LogLevelParser.Parse(value, LogEventLevel.Warning);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("information", LogEventLevel.Warning, LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Warning, LogEventLevel.Information)]
    public void Parse_KnownAliasShouldOverrideDefaultLevel(string value, LogEventLevel defaultLevel, LogEventLevel expected)
    {
        var result = LogLevelParser.Parse(value, defaultLevel);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, LogEventLevel.Information, LogEventLevel.Information)]
    [InlineData(null, LogEventLevel.Warning, LogEventLevel.Warning)]
    [InlineData("", LogEventLevel.Information, LogEventLevel.Information)]
    [InlineData("invalid", LogEventLevel.Information, LogEventLevel.Information)]
    [InlineData("garbage", LogEventLevel.Warning, LogEventLevel.Warning)]
    [InlineData("fatal", LogEventLevel.Fatal, LogEventLevel.Fatal)]
    public void Parse_ShouldReturnDefault_WhenValueDoesNotMatchKnownAlias(string? value, LogEventLevel defaultLevel, LogEventLevel expected)
    {
        var result = LogLevelParser.Parse(value, defaultLevel);
        Assert.Equal(expected, result);
    }
}