using AwesomeAssertions;
using Bunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The triage report shows code spans and bold of the agent's text as elements, in the seven places the agent
/// writes prose; the query, source, check and where stay as written.
/// </summary>
public class TriageReportInlineTextTests : BunitContext
{
    private const string Marked = "The `RefactoringExecutor` skips **Fix** here";
    private const string MarkedHtml = "The <code class=\"inline-code\">RefactoringExecutor</code> skips <strong>Fix</strong> here";

    private static TriageResult Result() => new()
    {
        Verdict = TriageVerdict.CauseFound,
        Summary = Marked,
        Impact = "Hits `orders` for **37** customers",
        Reproduced = true,
        Reproduction = "Run `dotnet test` **twice**",
        CausalChain =
        [
            new TriageChainStep { Text = "Step with `Ack()` and **order**", Kind = TriageChainKind.RootCause, EvidenceIds = ["E1"] },
        ],
        Evidence =
        [
            new TriageEvidence { Id = "E1", Claim = "Claim about `Ack()` and **line 58**", Source = "`src` **code**", Query = "`raw` **query**" },
        ],
        Investigated =
        [
            new TriageCheck { Check = "`check` **name**", Where = "`where` **here**", Result = "Result of `Ack()` is **late**", EvidenceIds = ["E1"] },
        ],
        NotChecked = [new TriageGap { What = "`What` **gap**", Why = "No `trace` source is **connected**" }],
    };

    private IRenderedComponent<TriageReport> RenderReport(TriageResult? result = null) =>
        Render<TriageReport>(p => p.Add(c => c.Result, result ?? Result()));

    [Fact]
    public void Summary_ShowsCodeAndBoldAsElements()
    {
        var cut = RenderReport();

        cut.Find(".triage-cause").InnerHtml.Should().Be(MarkedHtml);
    }

    [Fact]
    public void ImpactAndReproduction_KeepTheirPrefixAndShowCodeAndBold()
    {
        var cut = RenderReport();

        var lines = cut.FindAll("[data-testid='triage-verdict'] p.triage-muted");

        lines.Should().HaveCount(2);
        lines[0].InnerHtml.Should().Be("Impact: Hits <code class=\"inline-code\">orders</code> for <strong>37</strong> customers");
        lines[1].InnerHtml.Should().Be("Reproduction: Run <code class=\"inline-code\">dotnet test</code> <strong>twice</strong>");
    }

    [Fact]
    public void CausalChainStep_ShowsCodeAndBold()
    {
        var cut = RenderReport();

        var step = cut.Find("[data-testid='triage-chain'] li");

        step.QuerySelector("code.inline-code")!.TextContent.Should().Be("Ack()");
        step.QuerySelector("strong")!.TextContent.Should().Be("order");
        step.TextContent.Should().Contain("Step with Ack() and order").And.NotContain("`").And.NotContain("**");
        step.QuerySelector(".triage-eid")!.TextContent.Should().Be("E1");
    }

    [Fact]
    public void EvidenceClaim_ShowsCodeAndBold_ButQueryAndSourceStayAsWritten()
    {
        var cut = RenderReport();

        var cells = cut.FindAll("[data-testid='triage-evidence'] tbody tr td");

        cells[1].QuerySelector("code.inline-code")!.TextContent.Should().Be("Ack()");
        cells[1].QuerySelector("strong")!.TextContent.Should().Be("line 58");
        cells[1].QuerySelector(".triage-q")!.TextContent.Should().Be("`raw` **query**");
        cells[1].QuerySelector(".triage-q code, .triage-q strong").Should().BeNull();
        cells[2].TextContent.Should().Contain("`src` **code**");
        cells[2].QuerySelector("code, strong").Should().BeNull();
    }

    [Fact]
    public void CheckResult_ShowsCodeAndBold_ButCheckAndWhereStayAsWritten()
    {
        var cut = RenderReport();

        var cells = cut.FindAll("[data-testid='triage-investigated'] tbody tr td");

        cells[1].TextContent.Should().Be("`check` **name**");
        cells[2].TextContent.Should().Be("`where` **here**");
        cells[1].QuerySelector("code, strong").Should().BeNull();
        cells[2].QuerySelector("code, strong").Should().BeNull();
        cells[4].QuerySelector("code.inline-code")!.TextContent.Should().Be("Ack()");
        cells[4].QuerySelector("strong")!.TextContent.Should().Be("late");
    }

    [Fact]
    public void NotCheckedWhy_ShowsCodeAndBold_ButWhatStaysAsWritten()
    {
        var cut = RenderReport();

        var gap = cut.Find("[data-testid='triage-investigated'] ul li");

        gap.InnerHtml.Should().Be(
            "<strong>`What` **gap**</strong>: No <code class=\"inline-code\">trace</code> source is <strong>connected</strong>");
    }

    [Fact]
    public void AgentHtmlInAnyOfTheSevenFields_NeverBecomesAnElement()
    {
        const string attack = "<script>alert(1)</script> `<img src=x onerror=alert(1)>` **<b>x</b>**";
        var result = new TriageResult
        {
            Verdict = TriageVerdict.CauseFound,
            Summary = attack,
            Impact = attack,
            Reproduced = true,
            Reproduction = attack,
            CausalChain = [new TriageChainStep { Text = attack }],
            Evidence = [new TriageEvidence { Id = "E1", Claim = attack, Source = "code" }],
            Investigated = [new TriageCheck { Check = "c", Where = "w", Result = attack }],
            NotChecked = [new TriageGap { What = "w", Why = attack }],
        };

        var cut = RenderReport(result);

        cut.FindAll("script, img, b").Should().BeEmpty();
        cut.FindAll("code.inline-code").Should().HaveCount(7);
        cut.Markup.Should().NotContain("<script");
    }

    [Fact]
    public void ReportWithoutMarks_ShowsTheTextUnchanged()
    {
        var result = new TriageResult
        {
            Verdict = TriageVerdict.Inconclusive,
            Summary = "Logins are slow in the morning.\nNo single cause was found.",
        };

        var cut = RenderReport(result);

        cut.Find(".triage-cause").InnerHtml.Should().Be("Logins are slow in the morning.\nNo single cause was found.");
        cut.FindAll("code.inline-code").Should().BeEmpty();
    }
}
