namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

public class BrainLineFormatTests
{
    private static readonly string[] _dotnetLabels = ["dotnet"];

    // ── TryParseSkillLine ─────────────────────────────────────────────

    [Theory]
    [InlineData("- [2+] For a situation, do something (#1).", true, "2+", "#1")]
    [InlineData("- [user] For a situation, do something (owner/repo#1).", true, "user", "owner/repo#1")]
    [InlineData("- [2+] For a situation, do something (#1, owner/repo#2).", true, "2+", "#1")]
    [InlineData("- [invalid] For a situation, do something (#1).", false, null, null)]
    [InlineData("- [2+] For something (#1).", false, null, null)]  // missing comma separator
    [InlineData("not a line", false, null, null)]
    public void TryParseSkillLine_VariousInputs(
        string line, bool expectedSuccess, string? expectedTag, string? expectedFirstRef)
    {
        var ok = BrainLineFormat.TryParseSkillLine(line, out var parsed);
        ok.Should().Be(expectedSuccess);
        if (expectedSuccess)
        {
            parsed.Tag.Should().Be(expectedTag);
            parsed.Refs[0].Should().Be(expectedFirstRef);
        }
    }

    [Fact]
    public void TryParseSkillLine_ExactlyMaxLength_ReturnsTrue()
    {
        // Build a line that's exactly 300 chars
        var body = new string('x', 1);
        var line = $"- [2+] For {body}, do something (#1).";
        var padded = line.PadRight(BrainLayout.MaxLineLength);
        // 300 chars should succeed; we just need the pattern to match
        // Build a valid 300-char line
        var situation = new string('a', 10);
        var action = new string('b', 10);
        var valid300 = $"- [2+] For {situation}, {action} (#1).";
        valid300 = valid300.PadRight(300)[..300];
        // Actually build one that's exactly 300 but valid format
        var template = "- [2+] For aaaa, bbbb (#1).";
        var padding = new string('a', 300 - template.Length);
        var exact300 = $"- [2+] For aaaa{padding}, bbbb (#1).";
        BrainLineFormat.TryParseSkillLine(exact300, out _).Should().BeTrue();
    }

    [Fact]
    public void TryParseSkillLine_OverMaxLength_ReturnsFalse()
    {
        var line = "- [2+] For " + new string('a', 300) + ", something (#1).";
        BrainLineFormat.TryParseSkillLine(line, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseSkillLine_WithMissedAgain_ParsesRefs()
    {
        var line = "- [2+] For a situation, do something (#1). missed again #2";
        var ok = BrainLineFormat.TryParseSkillLine(line, out var parsed);
        ok.Should().BeTrue();
        parsed.MissedAgainRefs.Should().ContainSingle().Which.Should().Be("#2");
    }

    [Fact]
    public void TryParseSkillLine_VeryLongLine_ReturnsFalseQuickly()
    {
        var line = "- [2+] For " + new string('a', 50_000) + ", something (#1).";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = BrainLineFormat.TryParseSkillLine(line, out _);
        sw.Stop();
        result.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeLessThan(500);
    }

    // ── TryParseCandidate ─────────────────────────────────────────────

    [Theory]
    [InlineData("- [repository] For a situation, do something (evidence).", true)]
    [InlineData("- [general] For a situation, do something (evidence).", true)]
    [InlineData("- [technology:dotnet] For a situation, do something (evidence).", true)]
    [InlineData("- [technology:other] For a situation, do something (evidence).", false)]  // label not in stack
    [InlineData("- [invalid] For a situation, do something (evidence).", false)]
    public void TryParseCandidate_VariousInputs(string line, bool expected)
    {
        var ok = BrainLineFormat.TryParseCandidate(line, _dotnetLabels, out _);
        ok.Should().Be(expected);
    }

    [Fact]
    public void TryParseCandidate_WithLineBreak_ReturnsFalse()
    {
        var line = "- [repository] For a situation,\n do something (evidence).";
        BrainLineFormat.TryParseCandidate(line, _dotnetLabels, out _).Should().BeFalse();
    }

    // ── CheckSkillFile ────────────────────────────────────────────────

    [Fact]
    public void CheckSkillFile_ValidFile_ReturnsTrue()
    {
        var content = """
            <!-- brain:v2 skill -->
            # My Skill

            ## Tests
            - [2+] For a situation, do something (#1).
            """;
        var result = BrainLineFormat.CheckSkillFile(content, 40);
        result.IsValid.Should().BeTrue();
        result.Lines.Should().HaveCount(1);
    }

    [Fact]
    public void CheckSkillFile_NoMarker_ReturnsMissingMarker()
    {
        var content = "# My Skill\n- [2+] For a, b (#1).";
        var result = BrainLineFormat.CheckSkillFile(content, 40);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("missing_marker");
    }

    [Fact]
    public void CheckSkillFile_OverLineBudget_ReturnsOverBudget()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!-- brain:v2 skill -->");
        sb.AppendLine("# Skill");
        sb.AppendLine("## Section");
        for (int i = 1; i <= 5; i++)
            sb.AppendLine($"- [2+] For situation {i}, do something (#{i}).");
        var result = BrainLineFormat.CheckSkillFile(sb.ToString(), 3);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("over_budget");
    }

    [Fact]
    public void CheckSkillFile_CrlfFile_IsAccepted()
    {
        var content = "<!-- brain:v2 skill -->\r\n# Skill\r\n## Section\r\n- [2+] For a, b (#1).\r\n";
        var result = BrainLineFormat.CheckSkillFile(content, 40);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CheckSkillFile_SecondTitle_ReturnsInvalidLine()
    {
        var content = "<!-- brain:v2 skill -->\n# Title1\n# Title2\n";
        var result = BrainLineFormat.CheckSkillFile(content, 40);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be("invalid_line");
    }

    // ── FindSharedContentViolation ────────────────────────────────────

    [Theory]
    [InlineData("see https://example.com for details", "url")]
    [InlineData("edit src/A.cs here", "path")]
    [InlineData("path a/b/c is important", "path")]
    public void FindSharedContentViolation_Detects(string body, string expected)
    {
        BrainLineFormat.FindSharedContentViolation(body, []).Should().Be(expected);
    }

    [Theory]
    [InlineData("use and/or logic")]
    [InlineData("follow CI/CD practices")]
    [InlineData("check A.cs file")]  // single file name without / is not a path
    [InlineData("follow a/b pattern")]  // one slash, no file extension = not a path
    public void FindSharedContentViolation_AllowedTokens_ReturnsNull(string body)
    {
        BrainLineFormat.FindSharedContentViolation(body, []).Should().BeNull();
    }

    [Fact]
    public void FindSharedContentViolation_LongRepoName_DetectsRepository()
    {
        // Req D23: names shorter than 4 chars are not checked
        BrainLineFormat.FindSharedContentViolation("use api tool", ["api"]).Should().BeNull();
        BrainLineFormat.FindSharedContentViolation("use coding-agent-automation patterns", ["coding-agent-automation"])
            .Should().Be("repository");
    }

    [Fact]
    public void FindSharedContentViolation_RepoNameInsideOtherWord_NotDetected()
    {
        // Whole-word check: "myrepo" should not match "repo" (if "repo" is only 4 chars)
        BrainLineFormat.FindSharedContentViolation("use myrepo-helper", ["repo"]).Should().BeNull();
    }
}
