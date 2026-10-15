using AwesomeAssertions;
using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;
using static CodingAgent.Web.UnitTests.Components.TriageTestData;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary><see cref="TriageDisplay"/> and the triage group of <see cref="AttentionAggregator"/>. The preview builder moved to the API (spec 051 2.7).</summary>
public class TriageDisplayTests
{
    [Fact]
    public void EveryStatus_HasALabelAndATone()
    {
        foreach (var status in Enum.GetValues<TriageStatus>())
        {
            TriageDisplay.StatusLabel(status).Should().NotBe(status.ToString().ToLowerInvariant());
            TriageDisplay.StatusTone(status).Should().BeOneOf("run", "warn", "ok", "err", "mute");
        }
    }

    [Fact]
    public void ChecksBySource_GroupsByThePartBeforeTheDot_MostFirst()
    {
        var checks = new[]
        {
            new TriageCheck { Check = "a", Where = "grafana · Loki", Result = "r" },
            new TriageCheck { Check = "b", Where = "Grafana · Prometheus", Result = "r" },
            new TriageCheck { Check = "c", Where = "sonarqube", Result = "r" },
            new TriageCheck { Check = "d", Where = " ", Result = "r" },
        };

        TriageDisplay.ChecksBySource(checks).Should().Equal(("grafana", 2), ("sonarqube", 1));
    }

    [Theory]
    [InlineData("https://grafana.example/d/1", "https://grafana.example/d/1")]
    [InlineData("http://intranet/x", "http://intranet/x")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/relative", null)]
    [InlineData(null, null)]
    public void SafeLink_AllowsOnlyAbsoluteHttp(string? url, string? expected) =>
        TriageDisplay.SafeLink(url).Should().Be(expected);

    // ── Attention ────────────────────────────────────────────────────────────

    [Fact]
    public void Attention_KeepsTriagesThatNeedSomeone_AndCountsThem()
    {
        var result = AttentionAggregator.Aggregate([], [
            ListItem(TriageStatus.NeedsReview), ListItem(TriageStatus.NeedsInput), ListItem(TriageStatus.Investigating), ListItem(TriageStatus.IssuesCreated),
        ]);

        result.TriagesNeedingYou.Select(t => t.Status).Should().Equal(TriageStatus.NeedsReview, TriageStatus.NeedsInput);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public void Attention_WithoutTriages_CountsAsBefore() =>
        AttentionAggregator.Aggregate([]).TotalCount.Should().Be(0);

    [Fact]
    public async Task Attention_TriageApiDown_DegradesToNoTriages()
    {
        var client = new Mock<CodingAgent.Web.Ui.IUiTriageClient>();
        client.Setup(c => c.ListAsync(It.IsAny<TriageListQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        (await AttentionAggregator.LoadTriagesNeedingYouAsync(client.Object, "", CancellationToken.None)).Should().BeEmpty();
        client.Verify(c => c.ListAsync(It.Is<TriageListQuery>(q => q.ProjectId == null && q.Tab == TriageListTab.NeedYou), It.IsAny<CancellationToken>()));
    }
}
