namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

public class CiLogEvidenceExtractorTests
{
    [Fact]
    public void ExtractFailedTestNames_BasicPattern()
    {
        var log = """
            2024-01-01T00:00:00Z   Failed MyTests.TestA [3 ms]
            2024-01-01T00:00:01Z   Failed MyTests.TestB
            """;
        var names = CiLogEvidenceExtractor.ExtractFailedTestNames(log);
        names.Should().BeEquivalentTo(["MyTests.TestA", "MyTests.TestB"]);
    }

    [Fact]
    public void ExtractFailedTestNames_StripsTimestampAndShardPrefix()
    {
        var log = "2026-10-26T12:00:00.000Z [0]   Failed Ns.Class.Method [5 ms]";
        var names = CiLogEvidenceExtractor.ExtractFailedTestNames(log);
        names.Should().ContainSingle().Which.Should().Be("Ns.Class.Method");
    }

    [Fact]
    public void ExtractFailedTestNames_SummaryLine_NotMatched()
    {
        var log = "Failed!  - Failed: 1, Passed: 10, Total: 11";
        var names = CiLogEvidenceExtractor.ExtractFailedTestNames(log);
        names.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFailedTestNames_CappedAt20()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 25; i++)
            sb.AppendLine($"  Failed Test_{i}");
        var names = CiLogEvidenceExtractor.ExtractFailedTestNames(sb.ToString());
        names.Should().HaveCount(BrainLayout.MaxFailedTestNames);
    }

    [Fact]
    public void Extract_QualityGateStatusLine_Kept()
    {
        var log = "Some output\nQUALITY GATE STATUS: FAILED\nMore output";
        var job = CiLogEvidenceExtractor.Extract("SonarQube", [], log);
        job.Lines.Should().ContainSingle().Which.Should().Contain("QUALITY GATE STATUS");
    }

    [Fact]
    public void Extract_ErrorLines_KeptUpToThree()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 5; i++)
            sb.AppendLine($"##[error]error {i}");
        var job = CiLogEvidenceExtractor.Extract("Build", [], sb.ToString());
        job.Lines.Where(l => l.StartsWith("##[error]")).Should().HaveCount(3);
    }

    [Fact]
    public void Extract_LargeLog_CompletesQuickly()
    {
        // 5 MB log
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 100_000; i++)
            sb.AppendLine($"line {i}: some output without matches to slow things down");
        var log = sb.ToString();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CiLogEvidenceExtractor.Extract("Job", [], log);
        sw.Stop();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(2);
    }

    [Fact]
    public void Extract_ParameterisedTestName_Parsed()
    {
        var log = "  Failed Ns.Class.Method(x: 1) [3 ms]";
        var names = CiLogEvidenceExtractor.ExtractFailedTestNames(log);
        names.Should().ContainSingle().Which.Should().Be("Ns.Class.Method(x: 1)");
    }

    [Fact]
    public void Extract_JobName_Preserved()
    {
        var job = CiLogEvidenceExtractor.Extract("My Job", ["Step 1"], null);
        job.Name.Should().Be("My Job");
        job.FailedSteps.Should().ContainSingle().Which.Should().Be("Step 1");
    }
}
