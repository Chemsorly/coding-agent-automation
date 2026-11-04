namespace CodingAgent.Agent.UnitTests.Executors;

using System.Text;
using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using Moq;
using Serilog;
using Xunit;

/// <summary>
/// Tests for <see cref="BrainReviewInputWriter.WriteAsync"/> (spec 054, Task 7A.3).
/// The model builder is private, so every test asserts on the rendered review input file.
/// Uses temp directories; no Docker (T12).
/// </summary>
public sealed class BrainReviewInputWriterTests : IDisposable
{
    private const string GroupSection = "Brain group";
    private const string InboxSection = "Inbox files";
    private const string MalformedSection = "Malformed inbox files (excluded)";
    private const string SkillsSection = "Skills at injected commits";

    private static readonly DateOnly DefaultDate = new(2026, 1, 1);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _workspace;
    private readonly Mock<IBrainUpdateService> _brainSvc;
    private readonly BrainReviewInputWriter _sut;
    private static readonly ILogger NullLogger = new LoggerConfiguration().CreateLogger();

    public BrainReviewInputWriterTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"riw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _brainSvc = new Mock<IBrainUpdateService>();
        // Default setups for all synchronous/async methods called in BuildModelAsync
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<string>)[]);
        _brainSvc.Setup(s => s.ReadFileAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                 .Returns((string?)null);
        _brainSvc.Setup(s => s.ReadFileAtHead(It.IsAny<string>(), It.IsAny<string>()))
                 .Returns((string?)null);
        _brainSvc.Setup(s => s.GetSkillCommitsSinceLastReview(It.IsAny<string>()))
                 .Returns((IReadOnlyList<BrainCommitInfo>)[]);
        _sut = new BrainReviewInputWriter(_brainSvc.Object, NullLogger);
    }

    private string OutputPath => Path.Combine(_workspace, AgentWorkspacePaths.BrainReviewInputFilePath);

    [Fact]
    public async Task WriteAsync_EmptyBrain_WritesOutputFile()
    {
        await _sut.WriteAsync(_workspace, null, CancellationToken.None);

        File.Exists(OutputPath).Should().BeTrue("WriteAsync must create the review input file");
        var content = await File.ReadAllTextAsync(OutputPath);
        content.Should().Contain("# Brain review input");
    }

    [Fact]
    public async Task WriteAsync_WithInboxFile_IncludesInboxInOutput()
    {
        var inboxRel = WriteInbox("run-1", InboxText("run-1"));
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, InboxSection).Should().Contain($"### {inboxRel}",
            "a well-formed inbox file must be listed as an inbox file");
        Section(content, MalformedSection).Should().BeEmpty(
            "a well-formed inbox file must not be reported as malformed");
    }

    // ── Malformed inbox guards ────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_InboxFileOverMaxBytes_ListedAsMalformedOverSize()
    {
        var inboxRel = WriteInbox("big", PadToBytes(InboxText("big"), BrainLayout.MaxInboxBytes + 1));
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, MalformedSection).Should().Contain($"- {inboxRel}: over 16 KB");
        Section(content, InboxSection).Should().NotContain(inboxRel,
            "an over-size inbox file must be excluded from the inbox file list");
    }

    [Fact]
    public async Task WriteAsync_InboxFileExactlyMaxBytes_IsAccepted()
    {
        var inboxRel = WriteInbox("edge", PadToBytes(InboxText("edge"), BrainLayout.MaxInboxBytes));
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, InboxSection).Should().Contain($"### {inboxRel}",
            "the size limit is inclusive: a file of exactly MaxInboxBytes is still read");
        Section(content, MalformedSection).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteAsync_InboxFileNotUtf8_ListedAsMalformedNotUtf8()
    {
        // Latin-1 'é' is the lone byte 0xE9, which is not valid UTF-8.
        var inboxRel = WriteInbox("latin1", Encoding.Latin1.GetBytes(InboxText("latin1") + "- café\n"));
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, MalformedSection).Should().Contain($"- {inboxRel}: not UTF-8");
        Section(content, InboxSection).Should().NotContain(inboxRel);
    }

    [Theory]
    [InlineData("no marker on the first line\n# Run r\n- repository: org/repo\n", "no marker")]
    [InlineData($"{BrainLayout.InboxMarker}\nrun: r\n- repository: org/repo\n", "broken header")]
    [InlineData($"{BrainLayout.InboxMarker}\n# Run r\n- outcome: succeeded\n", "broken header")]
    public async Task WriteAsync_InboxFileWithBrokenHeader_ListedAsMalformedWithParserError(
        string text, string expectedReason)
    {
        var inboxRel = WriteInbox("broken", text);
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, MalformedSection).Should().Contain($"- {inboxRel}: {expectedReason}",
            "the parser's error must be used as the malformed reason");
        Section(content, InboxSection).Should().NotContain(inboxRel);
    }

    [Fact]
    public async Task WriteAsync_TrackedInboxFileMissingOnDisk_ListedAsMalformedAndOthersKept()
    {
        var goodRel = WriteInbox("good", InboxText("good"));
        var missingRel = BrainLayout.InboxFileName(DefaultDate, "missing");
        TrackFiles(goodRel, missingRel);

        var content = await WriteAndReadAsync(null);

        var reasonLine = Section(content, MalformedSection)
            .Split('\n')
            .SingleOrDefault(l => l.StartsWith($"- {missingRel}: ", StringComparison.Ordinal));
        reasonLine.Should().NotBeNull("a file that cannot be read must be reported as malformed");
        reasonLine![$"- {missingRel}: ".Length..].Should().NotBeNullOrWhiteSpace(
            "the exception message must be used as the reason");
        Section(content, InboxSection).Should().Contain($"### {goodRel}",
            "a failure on one inbox file must not stop the others from being read");
    }

    [Fact]
    public async Task WriteAsync_ListTrackedFilesThrows_IsNonFatalAndWritesNoFile()
    {
        // T6: WriteAsync must not throw on service exceptions
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("no git"));

        var act = () => _sut.WriteAsync(_workspace, null, CancellationToken.None);
        await act.Should().NotThrowAsync("WriteAsync must be non-fatal (T6)");
        File.Exists(OutputPath).Should().BeFalse("no review input is written when the model cannot be built");
    }

    // ── Skills at injected commits ────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_InboxWithSkillsCommit_RendersRuleLinesAtThatCommit()
    {
        const string sha = "abc123def4567890abc123def4567890abc12345";
        var first = WriteInbox("run-a", InboxText("run-a", skillsInjectedFrom: $"{sha} ({BrainLayout.GeneralSkillPath})"));
        var second = WriteInbox("run-b", InboxText("run-b", skillsInjectedFrom: $"{sha} ({BrainLayout.GeneralSkillPath})"));
        TrackFiles(first, second);
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath))
                 .Returns($"{BrainLayout.SkillMarker}\n# General\n\n- [2+] For a situation, do something (#1).\n  - [1] Indented rule (#2).\nplain prose line\n");

        var content = await WriteAndReadAsync(null);

        var skills = Section(content, SkillsSection);
        skills.Should().Contain($"### Commit {sha[..12]}");
        skills.Should().Contain($"#### {BrainLayout.GeneralSkillPath}");
        skills.Should().Contain("- [2+] For a situation, do something (#1).");
        skills.Should().Contain("  - [1] Indented rule (#2).");
        skills.Should().NotContain("plain prose line", "only rule lines are kept");
        skills.Should().NotContain(BrainLayout.SkillMarker, "only rule lines are kept");
        skills.Should().NotContain("unknown commit");
        _brainSvc.Verify(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath), Times.Once,
            "a commit shared by several inbox files is read once");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("# General\nplain prose without rule lines\n")]
    public async Task WriteAsync_SkillsCommitWithNoRuleLines_RendersUnknownCommit(string? skillContent)
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var inboxRel = WriteInbox("run-a", InboxText("run-a", skillsInjectedFrom: $"{sha} ({BrainLayout.GeneralSkillPath})"));
        TrackFiles(inboxRel);
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath))
                 .Returns(skillContent);

        var content = await WriteAndReadAsync(null);

        var skills = Section(content, SkillsSection);
        skills.Should().Contain($"### Commit {sha[..12]}\nunknown commit");
        skills.Should().NotContain("####", "no skill file has rule lines at that commit");
    }

    [Fact]
    public async Task WriteAsync_HeaderListsRepositoryAndTechnologySkills_RendersEachInjectedFileAtThatCommit()
    {
        const string sha = "fedcba9876543210fedcba9876543210fedcba98";
        const string repoSkill = "projects/repo/SKILL.md";
        const string techSkill = "technology/dotnet/SKILL.md";
        var inboxRel = WriteInbox("run-a", InboxText("run-a",
            skillsInjectedFrom: $"{sha} ({BrainLayout.GeneralSkillPath}, {repoSkill}, {techSkill})"));
        TrackFiles(inboxRel);
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath))
                 .Returns("- [1] General rule (#1).\n");
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, repoSkill))
                 .Returns("- [1] Repository rule (#2).\n");
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, techSkill))
                 .Returns("- [1] Technology rule (#3).\n");

        var content = await WriteAndReadAsync(null);

        var skills = Section(content, SkillsSection);
        skills.Should().Contain($"### Commit {sha[..12]}");
        skills.Should().Contain($"#### {BrainLayout.GeneralSkillPath}\n- [1] General rule (#1).");
        skills.Should().Contain($"#### {repoSkill}\n- [1] Repository rule (#2).");
        skills.Should().Contain($"#### {techSkill}\n- [1] Technology rule (#3).");
        skills.Should().NotContain("unknown commit");
        _brainSvc.Verify(s => s.ReadFileAtCommit(It.IsAny<string>(), It.IsAny<string>(), BrainLayout.RepositorySkillPath("")),
            Times.Never, "the repository skill path is taken from the header, never built from an empty short name");
    }

    [Fact]
    public async Task WriteAsync_SeveralInboxFilesNameSameCommit_ReadsUnionOfTheirInjectedFiles()
    {
        const string sha = "1111111111222222222233333333334444444444";
        const string alphaSkill = "projects/alpha/SKILL.md";
        const string betaSkill = "projects/beta/SKILL.md";
        var alpha = WriteInbox("alpha", InboxText("alpha", "org/alpha",
            skillsInjectedFrom: $"{sha} ({alphaSkill}, {BrainLayout.GeneralSkillPath})"));
        var beta = WriteInbox("beta", InboxText("beta", "org/beta",
            skillsInjectedFrom: $"{sha} ({betaSkill}, {BrainLayout.GeneralSkillPath})"));
        TrackFiles(alpha, beta);
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, It.IsAny<string>()))
                 .Returns((string _, string _, string path) => $"- [1] Rule from {path} (#1).\n");

        var content = await WriteAndReadAsync(null);

        var skills = Section(content, SkillsSection);
        skills.Split('\n').Count(l => l.StartsWith("### Commit ", StringComparison.Ordinal)).Should().Be(1);
        skills.Should().Contain($"#### {alphaSkill}");
        skills.Should().Contain($"#### {betaSkill}");
        skills.Should().Contain($"#### {BrainLayout.GeneralSkillPath}");
        foreach (var path in new[] { alphaSkill, betaSkill, BrainLayout.GeneralSkillPath })
            _brainSvc.Verify(s => s.ReadFileAtCommit(_workspace, sha, path), Times.Once,
                "a file listed by several inbox files for the same commit is read once");
    }

    [Fact]
    public async Task WriteAsync_HeaderListsNonSkillPaths_ReadsOnlySkillPaths()
    {
        const string sha = "aaaaaaaaaabbbbbbbbbbccccccccccdddddddddd";
        var inboxRel = WriteInbox("run-a", InboxText("run-a",
            skillsInjectedFrom: $"{sha} ({BrainLayout.ReadmePath}, {BrainLayout.GeneralSkillPath}, {BrainLayout.PendingPath}, projects/a/b/SKILL.md)"));
        TrackFiles(inboxRel);
        _brainSvc.Setup(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath))
                 .Returns("- [1] General rule (#1).\n");

        var content = await WriteAndReadAsync(null);

        Section(content, SkillsSection).Should().Contain($"#### {BrainLayout.GeneralSkillPath}");
        _brainSvc.Verify(s => s.ReadFileAtCommit(_workspace, sha, BrainLayout.GeneralSkillPath), Times.Once);
        _brainSvc.Verify(s => s.ReadFileAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once,
            "paths in the header that are not skill paths are never read");
    }

    [Fact]
    public async Task WriteAsync_InboxWithoutSkillsCommit_RendersNoCommits()
    {
        var inboxRel = WriteInbox("run-a", InboxText("run-a", skillsInjectedFrom: "none"));
        TrackFiles(inboxRel);

        var content = await WriteAndReadAsync(null);

        Section(content, SkillsSection).Should().NotContain("### Commit");
        _brainSvc.Verify(s => s.ReadFileAtCommit(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WriteAsync_MoreSkillsCommitsThanBudget_RendersOnlyNewestCommits()
    {
        var count = BrainLayout.MaxInjectedCommitsInReview + 1;
        var shas = Enumerable.Range(0, count).Select(i => $"{i:D2}{new string('a', 38)}").ToList();
        var paths = shas.Select((sha, i) =>
                WriteInbox($"run-{i:D2}", InboxText($"run-{i:D2}", skillsInjectedFrom: $"{sha} ({BrainLayout.GeneralSkillPath})")))
            .ToArray();
        TrackFiles(paths);

        var content = await WriteAndReadAsync(null);

        var skills = Section(content, SkillsSection);
        skills.Split('\n').Count(l => l.StartsWith("### Commit ", StringComparison.Ordinal))
              .Should().Be(BrainLayout.MaxInjectedCommitsInReview);
        // Inbox files are taken newest first, so the commit of the oldest file (run-00) is dropped.
        skills.Should().NotContain($"### Commit {shas[0][..12]}");
        skills.Should().Contain($"### Commit {shas[^1][..12]}");
        _brainSvc.Verify(s => s.ReadFileAtCommit(It.IsAny<string>(), shas[0], It.IsAny<string>()), Times.Never);
    }

    // ── Group derivation ──────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_NoGroupGiven_DerivesGroupFromInboxHeaders(bool passEmptyList)
    {
        // Newest first: org/alpha wins over its case variant, which only contributes a duplicate.
        var alpha = WriteInbox("alpha", InboxText("alpha", "org/alpha", "Alpha", "dotnet, docker"), new DateOnly(2026, 3, 2));
        var alphaDup = WriteInbox("alpha-dup", InboxText("alpha-dup", "Org/Alpha", "Ignored"), new DateOnly(2026, 3, 1));
        var beta = WriteInbox("beta", InboxText("beta", "org/beta"), new DateOnly(2026, 2, 1));
        TrackFiles(beta, alphaDup, alpha);

        var content = await WriteAndReadAsync(passEmptyList ? [] : null);

        var group = Section(content, GroupSection);
        group.Should().NotContain("No group information available.");
        group.Should().Contain("| org/alpha | alpha | Alpha | dotnet, docker |");
        group.Should().Contain("| org/beta | beta |  |  |");
        group.Should().NotContain("Org/Alpha", "repositories are de-duplicated case-insensitively");
        group.Should().NotContain("Ignored", "project and stack labels come from the newest inbox file of the repository");
    }

    [Fact]
    public async Task WriteAsync_GroupGiven_DoesNotDeriveFromInboxHeaders()
    {
        var inboxRel = WriteInbox("other", InboxText("other", "org/other"));
        TrackFiles(inboxRel);
        var group = new List<BrainGroupMember>
        {
            new() { RepositoryFullName = "org/repo", RepositoryShortName = "repo", ProjectName = "P", StackLabels = ["dotnet"] }
        };

        var content = await WriteAndReadAsync(group);

        Section(content, GroupSection).Should().Contain("| org/repo | repo | P | dotnet |");
        Section(content, GroupSection).Should().NotContain("org/other", "a given group is used as is");
        Section(content, InboxSection).Should().Contain("- repository: org/other");
    }

    [Fact]
    public async Task WriteAsync_WithGroup_IncludesGroupInOutput()
    {
        var group = new List<BrainGroupMember>
        {
            new BrainGroupMember { RepositoryFullName = "org/repo", RepositoryShortName = "repo", ProjectName = "P", StackLabels = ["dotnet"] }
        };

        await _sut.WriteAsync(_workspace, group, CancellationToken.None);

        var content = await File.ReadAllTextAsync(OutputPath);
        content.Should().Contain("org/repo");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>An inbox file in the format <see cref="BrainInboxComposer"/> writes.</summary>
    private static string InboxText(
        string runId,
        string repository = "org/repo",
        string? project = null,
        string stackLabels = "none",
        string skillsInjectedFrom = "none") =>
        $"{BrainLayout.InboxMarker}\n# Run {runId}\n"
        + "- run type: implementation\n- outcome: succeeded\n"
        + $"- repository: {repository}\n"
        + (project is null ? "" : $"- project: {project}\n")
        + $"- stack labels: {stackLabels}\n- issue: #1\n"
        + $"- skills injected from: {skillsInjectedFrom}\n"
        + "\n## Candidates\n- [1] For a situation, do something (#1).\n";

    /// <summary>Pads ASCII inbox text with a trailing line so its UTF-8 size is exactly <paramref name="bytes"/>.</summary>
    private static byte[] PadToBytes(string text, int bytes)
    {
        var head = Utf8NoBom.GetBytes(text + "\n");
        head.Length.Should().BeLessThan(bytes);
        var padded = Utf8NoBom.GetBytes(text + "\n" + new string('x', bytes - head.Length));
        padded.Length.Should().Be(bytes);
        return padded;
    }

    private string WriteInbox(string runId, string text, DateOnly? date = null) =>
        WriteInbox(runId, Utf8NoBom.GetBytes(text), date);

    private string WriteInbox(string runId, byte[] bytes, DateOnly? date = null)
    {
        var rel = BrainLayout.InboxFileName(date ?? DefaultDate, runId);
        var full = Path.Combine(_workspace, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return rel;
    }

    private void TrackFiles(params string[] paths) =>
        _brainSvc.Setup(s => s.ListTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IReadOnlyList<string>)paths);

    private async Task<string> WriteAndReadAsync(IReadOnlyList<BrainGroupMember>? group)
    {
        await _sut.WriteAsync(_workspace, group, CancellationToken.None);
        File.Exists(OutputPath).Should().BeTrue("WriteAsync must create the review input file");
        return (await File.ReadAllTextAsync(OutputPath)).Replace("\r\n", "\n");
    }

    /// <summary>Returns the body of the <c>## heading</c> section, or "" when the section is absent.</summary>
    private static string Section(string content, string heading)
    {
        var marker = $"## {heading}\n";
        var start = content.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += marker.Length;
        var next = content.IndexOf("\n## ", start, StringComparison.Ordinal);
        return next < 0 ? content[start..] : content[start..(next + 1)];
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            foreach (var f in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
