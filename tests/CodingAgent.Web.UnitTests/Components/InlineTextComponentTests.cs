using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Components.Shared;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// <see cref="InlineText"/>: agent text with code spans and bold, built as elements from parsed tokens. Agent
/// output is untrusted, so nothing in it may reach the page as markup.
/// </summary>
public class InlineTextComponentTests : BunitContext
{
    private IRenderedComponent<InlineText> RenderText(string? text) =>
        Render<InlineText>(p => p.Add(c => c.Text, text));

    /// <summary>The component renders no wrapper element, so its text is that of its top-level nodes.</summary>
    private static string TextOf(IRenderedComponent<InlineText> cut) =>
        string.Concat(cut.Nodes.Select(n => n.TextContent));

    [Fact]
    public void InlineText_EncodesHtmlInAgentText()
    {
        var cut = RenderText("<script>alert(1)</script> `<b>x</b>`");

        cut.FindAll("script").Should().BeEmpty();
        cut.FindAll("b").Should().BeEmpty();
        cut.Find("code.inline-code").TextContent.Should().Be("<b>x</b>");
        TextOf(cut).Should().Be("<script>alert(1)</script> <b>x</b>");
        cut.Markup.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;").And.NotContain("<script");
    }

    [Fact]
    public void InlineText_EncodesHtmlInBold()
    {
        var cut = RenderText("**<img src=x onerror=alert(1)>**");

        cut.FindAll("img").Should().BeEmpty();
        cut.Find("strong").TextContent.Should().Be("<img src=x onerror=alert(1)>");
    }

    [Fact]
    public void InlineText_RendersCodeAsInlineCodeAndBoldAsStrong_WithoutWhitespaceBetweenTokens()
    {
        var cut = RenderText("A `x` B **y** C");

        cut.Markup.Should().Be("A <code class=\"inline-code\">x</code> B <strong>y</strong> C");
    }

    [Fact]
    public void InlineText_PlainText_HasNoElement()
    {
        var cut = RenderText("Nothing marked here.");

        cut.Markup.Should().Be("Nothing marked here.");
        cut.FindAll("code, strong").Should().BeEmpty();
    }

    [Fact]
    public void InlineText_UnclosedMarks_ShowAsTheyAreWritten()
    {
        var cut = RenderText("a `b and **c");

        TextOf(cut).Should().Be("a `b and **c");
        cut.FindAll("code, strong").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void InlineText_NullOrEmpty_RendersNothing(string? text) =>
        RenderText(text).Markup.Should().BeEmpty();

    [Fact]
    public void InlineText_KeepsNewlinesOfTheText()
    {
        var cut = RenderText("first line\nsecond `line`");

        TextOf(cut).Should().Be("first line\nsecond line");
    }

    [Fact]
    public void InlineText_ReRendersWhenTheTextChanges()
    {
        var cut = RenderText("`one`");

        cut.Render(p => p.Add(c => c.Text, "**two**"));

        cut.FindAll("code").Should().BeEmpty();
        cut.Find("strong").TextContent.Should().Be("two");
    }
}
