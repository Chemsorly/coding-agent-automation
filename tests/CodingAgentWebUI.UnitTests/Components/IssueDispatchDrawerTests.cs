using System.Reflection;
using CodingAgentWebUI.Components.Pages;

namespace CodingAgentWebUI.UnitTests.Components;

public class IssueDispatchDrawerTests
{
    private static string InvokeGetBodyPreview(string body)
    {
        var method = typeof(IssueDispatchDrawer).GetMethod("GetBodyPreview", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [body])!;
    }

    [Fact]
    public void GetBodyPreview_ReturnsFirstContentLine()
    {
        var result = InvokeGetBodyPreview("This is the first line\nSecond line");
        Assert.Equal("This is the first line", result);
    }

    [Fact]
    public void GetBodyPreview_SkipsBlankLines()
    {
        var result = InvokeGetBodyPreview("\n\n  \nActual content");
        Assert.Equal("Actual content", result);
    }

    [Fact]
    public void GetBodyPreview_SkipsDoubleHashHeadings()
    {
        var result = InvokeGetBodyPreview("## Summary\nThe real content");
        Assert.Equal("The real content", result);
    }

    [Fact]
    public void GetBodyPreview_StripsLeadingHash()
    {
        var result = InvokeGetBodyPreview("# Title\nBody text");
        Assert.Equal("Title", result);
    }

    [Fact]
    public void GetBodyPreview_ReturnsEmpty_WhenAllLinesAreHeadingsOrBlank()
    {
        var result = InvokeGetBodyPreview("## Heading\n## Another\n\n");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetBodyPreview_ReturnsEmpty_ForEmptyBody()
    {
        var result = InvokeGetBodyPreview("");
        Assert.Equal(string.Empty, result);
    }
}
