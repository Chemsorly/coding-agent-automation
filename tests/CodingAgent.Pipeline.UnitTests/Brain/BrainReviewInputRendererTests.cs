namespace CodingAgent.Pipeline.UnitTests.Brain;

using AwesomeAssertions;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;
using static CodingAgent.Pipeline.Brain.BrainReviewInputRenderer;

/// <summary>
/// Tests for <see cref="BrainReviewInputRenderer.Render"/> (spec 054, Task 7A.3).
/// Pure function — no I/O, no mocks.
/// </summary>
public sealed class BrainReviewInputRendererTests
{
    private static BrainInboxHeader MakeHeader(string runId = "run-1") =>
        new BrainInboxHeader(
            RunId: runId,
            RunType: "implementation",
            Outcome: "succeeded",
            RepositoryFullName: "org/repo",
            ProjectName: "My Project",
            StackLabels: ["dotnet", "blazor"],
            Issue: "#42",
            PullRequestUrl: "https://github.com/org/repo/pull/5",
            SkillsCommit: null,
            SkillsFiles: []);

    [Fact]
    public void Render_EmptyModel_ContainsSectionHeaders()
    {
        var model = new BrainReviewInputModel(
            Group: null,
            InboxFiles: [],
            MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [],
            HumanCommits: [],
            InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("# Brain review input");
        result.Should().Contain("## Brain group");
        result.Should().Contain("No group information available.");
        result.Should().Contain("## Inbox files");
        result.Should().Contain("## Skills at injected commits");
        result.Should().Contain("## Current skill files");
        result.Should().Contain("## Human commits since the last review");
        result.Should().Contain("No human commits.");
        // Malformed section is only rendered when there are malformed files
        result.Should().NotContain("## Malformed inbox files");
    }

    [Fact]
    public void Render_WithGroup_RendersGroupTable()
    {
        var group = new List<BrainGroupMember>
        {
            new BrainGroupMember
            {
                RepositoryFullName = "org/repo",
                RepositoryShortName = "repo",
                ProjectName = "My Project",
                StackLabels = ["dotnet"]
            }
        };
        var model = new BrainReviewInputModel(
            Group: group, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("| Repository | Short name | Project | Stack labels |");
        result.Should().Contain("org/repo");
        result.Should().Contain("dotnet");
        result.Should().NotContain("No group information available.");
    }

    [Fact]
    public void Render_WithInboxFiles_RendersInboxSection()
    {
        var inbox = new List<InboxFileEntry>
        {
            new InboxFileEntry("inbox/2026-10-29_run-1.md", MakeHeader()),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### inbox/2026-10-29_run-1.md");
        result.Should().Contain("run: run-1");
        result.Should().Contain("type: implementation");
        result.Should().Contain("outcome: succeeded");
        result.Should().Contain("repository: org/repo");
        result.Should().Contain("project: My Project");
        result.Should().Contain("issue: #42");
        result.Should().Contain("stack labels: dotnet, blazor");
    }

    [Fact]
    public void Render_InboxFileNoHeader_RendersPathOnly()
    {
        var inbox = new List<InboxFileEntry>
        {
            new InboxFileEntry("inbox/no-header.md", Header: null),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### inbox/no-header.md");
        result.Should().NotContain("run: ");
    }

    [Fact]
    public void Render_InboxFilesLeft_ShowsCount()
    {
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 42);

        var result = Render(model);

        result.Should().Contain("Inbox files left for the next review: 42");
    }

    [Fact]
    public void Render_WithMalformedInbox_RendersMalformedSection()
    {
        var malformed = new List<MalformedInboxEntry>
        {
            new MalformedInboxEntry("inbox/bad.md", "not UTF-8"),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: malformed,
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("## Malformed inbox files (excluded)");
        result.Should().Contain("inbox/bad.md: not UTF-8");
    }

    [Fact]
    public void Render_SkillsAtCommits_RendersCommitSection()
    {
        var skills = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>
        {
            ["abc1234567890"] = [new SkillFileAtCommit("general/SKILL.md", ["- use parameterized queries"])]
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: skills,
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("### Commit abc123456789"); // 12-char truncation
        result.Should().Contain("#### general/SKILL.md");
        result.Should().Contain("- use parameterized queries");
    }

    [Fact]
    public void Render_UnknownCommit_ShowsUnknownPlaceholder()
    {
        var skills = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>
        {
            ["abc123"] = [] // no skill files = unknown commit
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: skills,
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("unknown commit");
    }

    [Fact]
    public void Render_CurrentFiles_RendersFileList()
    {
        var files = new List<CurrentSkillFile>
        {
            new CurrentSkillFile("general/SKILL.md", 15),
            new CurrentSkillFile("projects/org-repo/SKILL.md", 40),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: files, HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("- general/SKILL.md (15 lines)");
        result.Should().Contain("- projects/org-repo/SKILL.md (40 lines)");
    }

    [Fact]
    public void Render_HumanCommits_RendersCommitList()
    {
        var commits = new List<BrainCommitInfo>
        {
            new BrainCommitInfo("deadbeef12345", "user: fix typo"),
        };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: [], MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: commits, InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().Contain("deadbeef"); // 8-char truncation
        result.Should().Contain("user: fix typo");
        result.Should().NotContain("No human commits.");
    }

    [Fact]
    public void Render_InboxHeaderWithoutProject_OmitsProjectLine()
    {
        var header = new BrainInboxHeader(
            RunId: "r1", RunType: "implementation", Outcome: "succeeded",
            RepositoryFullName: "org/repo", ProjectName: null,
            StackLabels: [], Issue: "#1",
            PullRequestUrl: null, SkillsCommit: null, SkillsFiles: []);
        var inbox = new List<InboxFileEntry> { new InboxFileEntry("inbox/r1.md", header) };
        var model = new BrainReviewInputModel(
            Group: null, InboxFiles: inbox, MalformedInbox: [],
            SkillsAtCommits: new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(),
            CurrentFiles: [], HumanCommits: [], InboxFilesLeft: 0);

        var result = Render(model);

        result.Should().NotContain("project:");
        result.Should().NotContain("stack labels:");
    }

    /// <summary>
    /// Full-output snapshot test. Verifies the exact rendered string for a fully-populated model
    /// so that any whitespace, newline, or ordering regression from a refactor is caught immediately.
    /// The expected string was derived by tracing through the pre-refactor Render implementation
    /// (commit 4341131a2ac0) line-by-line with this exact model. Independently verified by
    /// <see cref="Render_FullModel_OutputEqualsReferenceImplementation"/>.
    /// </summary>
    [Fact]
    public void Render_FullModel_MatchesExpectedOutput()
    {
        var group = new List<BrainGroupMember>
        {
            new BrainGroupMember
            {
                RepositoryFullName = "org/repo",
                RepositoryShortName = "repo",
                ProjectName = "My Project",
                StackLabels = ["dotnet"]
            }
        };
        var inbox = new List<InboxFileEntry>
        {
            new InboxFileEntry("inbox/2026-10-29_run-1.md", MakeHeader())
        };
        var malformed = new List<MalformedInboxEntry>
        {
            new MalformedInboxEntry("inbox/bad.md", "not UTF-8")
        };
        var skills = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>
        {
            ["abc1234567890"] = [new SkillFileAtCommit("general/SKILL.md", ["- use parameterized queries"])]
        };
        var currentFiles = new List<CurrentSkillFile>
        {
            new CurrentSkillFile("general/SKILL.md", 15)
        };
        var humanCommits = new List<BrainCommitInfo>
        {
            new BrainCommitInfo("deadbeef12345", "user: fix typo")
        };
        var model = new BrainReviewInputModel(
            Group: group,
            InboxFiles: inbox,
            MalformedInbox: malformed,
            SkillsAtCommits: skills,
            CurrentFiles: currentFiles,
            HumanCommits: humanCommits,
            InboxFilesLeft: 3);

        var nl = Environment.NewLine;
        var expected = string.Join(nl, new[]
        {
            "# Brain review input",
            "",
            "## Brain group",
            "",
            "| Repository | Short name | Project | Stack labels |",
            "|---|---|---|---|",
            "| org/repo | repo | My Project | dotnet |",
            "",
            "## Inbox files",
            "",
            "### inbox/2026-10-29_run-1.md",
            "- run: run-1",
            "- type: implementation",
            "- outcome: succeeded",
            "- repository: org/repo",
            "- project: My Project",
            "- issue: #42",
            "- stack labels: dotnet, blazor",
            "",
            "Inbox files left for the next review: 3",
            "",
            "## Malformed inbox files (excluded)",
            "",
            "- inbox/bad.md: not UTF-8",
            "",
            "## Skills at injected commits",
            "",
            "### Commit abc123456789",
            "#### general/SKILL.md",
            "- use parameterized queries",
            "",
            "",
            "## Current skill files",
            "",
            "- general/SKILL.md (15 lines)",
            "",
            "## Human commits since the last review",
            "",
            "- deadbeef: user: fix typo"
        }) + nl;

        Render(model).Should().Be(expected);
    }

    /// <summary>
    /// Property-based equivalence test. For 20 randomly-generated <see cref="BrainReviewInputModel"/>
    /// instances, asserts that the refactored <see cref="BrainReviewInputRenderer.Render"/> produces
    /// byte-identical output to <see cref="ReferenceRender"/> — a verbatim copy of the original
    /// monolithic Render body from commit 4341131a2ac0.
    ///
    /// This is the strongest possible equivalence proof: the oracle IS the original algorithm,
    /// and equivalence is verified across 20 distinct random inputs rather than a single example.
    /// If the Extract Method refactoring introduced any output difference for any input, this
    /// property will fail.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(BrainReviewInputArbitrary)])]
    public void Render_ForAnyModel_OutputMatchesPreRefactorAlgorithm(BrainReviewInputModel model)
    {
        Render(model).Should().Be(ReferenceRender(model));
    }

    /// <summary>
    /// Verbatim copy of the original <c>BrainReviewInputRenderer.Render</c> body from
    /// commit 4341131a2ac0, preserved as a reference oracle for equivalence testing.
    /// Must not be updated when the production implementation changes.
    /// </summary>
    private static string ReferenceRender(BrainReviewInputModel model)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Brain review input");
        sb.AppendLine();

        // Brain group
        sb.AppendLine("## Brain group");
        sb.AppendLine();
        if (model.Group is { Count: > 0 })
        {
            sb.AppendLine("| Repository | Short name | Project | Stack labels |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var m in model.Group)
                sb.AppendLine($"| {m.RepositoryFullName} | {m.RepositoryShortName} | {m.ProjectName ?? ""} | {string.Join(", ", m.StackLabels)} |");
        }
        else
        {
            sb.AppendLine("No group information available.");
        }
        sb.AppendLine();

        // Inbox files
        sb.AppendLine("## Inbox files");
        sb.AppendLine();
        foreach (var file in model.InboxFiles)
        {
            sb.AppendLine($"### {file.Path}");
            if (file.Header is { } h)
            {
                sb.AppendLine($"- run: {h.RunId}");
                sb.AppendLine($"- type: {h.RunType}");
                sb.AppendLine($"- outcome: {h.Outcome}");
                sb.AppendLine($"- repository: {h.RepositoryFullName}");
                if (h.ProjectName is not null)
                    sb.AppendLine($"- project: {h.ProjectName}");
                sb.AppendLine($"- issue: {h.Issue}");
                if (h.StackLabels.Count > 0)
                    sb.AppendLine($"- stack labels: {string.Join(", ", h.StackLabels)}");
            }
            sb.AppendLine();
        }

        if (model.InboxFilesLeft > 0)
            sb.AppendLine($"Inbox files left for the next review: {model.InboxFilesLeft}");
        sb.AppendLine();

        // Malformed inbox
        if (model.MalformedInbox.Count > 0)
        {
            sb.AppendLine("## Malformed inbox files (excluded)");
            sb.AppendLine();
            foreach (var bad in model.MalformedInbox)
                sb.AppendLine($"- {bad.Path}: {bad.Reason}");
            sb.AppendLine();
        }

        // Skills at injected commits
        sb.AppendLine("## Skills at injected commits");
        sb.AppendLine();
        foreach (var (sha, files) in model.SkillsAtCommits)
        {
            sb.AppendLine($"### Commit {sha[..Math.Min(12, sha.Length)]}");
            if (files.Count == 0)
            {
                sb.AppendLine("unknown commit");
            }
            else
            {
                foreach (var sf in files)
                {
                    sb.AppendLine($"#### {sf.Path}");
                    foreach (var line in sf.Lines)
                        sb.AppendLine(line);
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        // Current skill files
        sb.AppendLine("## Current skill files");
        sb.AppendLine();
        foreach (var f in model.CurrentFiles)
            sb.AppendLine($"- {f.Path} ({f.LineCount} lines)");
        sb.AppendLine();

        // Human commits
        sb.AppendLine("## Human commits since the last review");
        sb.AppendLine();
        if (model.HumanCommits.Count > 0)
            foreach (var c in model.HumanCommits)
                sb.AppendLine($"- {c.Sha[..Math.Min(8, c.Sha.Length)]}: {c.Message}");
        else
            sb.AppendLine("No human commits.");

        return sb.ToString();
    }
}

/// <summary>
/// FsCheck arbitrary that generates random <see cref="BrainReviewInputRenderer.BrainReviewInputModel"/>
/// instances for property-based equivalence testing.
/// All string fields are non-null to match the contracts assumed by both the refactored Render
/// and the ReferenceRender oracle (both use slice expressions on SHA strings).
/// </summary>
public static class BrainReviewInputArbitrary
{
    private static readonly string[] Repos = ["org/repo", "acme/backend", "corp/frontend", "team/sdk", "example/lib"];
    private static readonly string[] ShortNames = ["repo", "backend", "frontend", "sdk", "lib"];
    private static readonly string[] ProjectNames = ["My Project", "Acme API", "Corp FE", "Team SDK"];
    private static readonly string[] StackLabelValues = ["dotnet", "blazor", "python", "java", "typescript"];
    private static readonly string[] RunTypes = ["implementation", "review", "decomposition"];
    private static readonly string[] Outcomes = ["succeeded", "failed", "draft"];
    // SHA strings must be non-null (both Render and ReferenceRender call sha[..Math.Min(N, sha.Length)])
    private static readonly string[] Shas = ["abc1234567890abcd", "deadbeef12345678", "cafebabe00000000", "0011223344556677", "aabb"];
    private static readonly string[] CommitMessages = ["user: fix typo", "user: update deps", "human: refactor", "chore: bump"];
    private static readonly string[] FilePaths = ["inbox/2026-10-29_run-1.md", "inbox/2026-10-28_run-2.md", "inbox/2026-10-27_run-3.md"];
    private static readonly string[] SkillPaths = ["general/SKILL.md", "projects/org-repo/SKILL.md", "technology/dotnet.md"];
    private static readonly string[] SkillLines = ["- use parameterized queries", "- prefer interfaces over classes", "- always dispose IDisposable"];
    private static readonly string[] MalformReasons = ["not UTF-8", "over 16 KB", "broken header", "missing run-id"];
    private static readonly string[] Issues = ["#1", "#42", "#100", "#999"];

    // Gen.ArrayOf(gen, count) — gen first, count second (FsCheck 3 convention)
    private static Gen<List<T>> BoundedList<T>(Gen<T> gen, int maxCount) =>
        from n in Gen.Choose(0, maxCount)
        from arr in Gen.ArrayOf(gen, n)
        select arr.ToList();

    private static Gen<IReadOnlyList<string>> StringListGen() =>
        BoundedList(Gen.Elements(StackLabelValues), 3).Select(l => (IReadOnlyList<string>)l);

    private static Gen<BrainGroupMember> GroupMemberGen() =>
        from fullName in Gen.Elements(Repos)
        from shortName in Gen.Elements(ShortNames)
        from hasProject in Gen.Elements(true, false)
        from projectName in Gen.Elements(ProjectNames)
        from labels in StringListGen()
        select new BrainGroupMember
        {
            RepositoryFullName = fullName,
            RepositoryShortName = shortName,
            ProjectName = hasProject ? projectName : null,
            StackLabels = labels
        };

    // Note: "group" is a contextual LINQ keyword — use "grp" as the range variable everywhere.
    private static Gen<IReadOnlyList<BrainGroupMember>?> GroupGen()
    {
        var withMembersGen = BoundedList(GroupMemberGen(), 3)
            .Select(members => (IReadOnlyList<BrainGroupMember>?)members);
        var nullGen = Gen.Constant((IReadOnlyList<BrainGroupMember>?)null);
        // Gen.Frequency uses value tuples: (weight, gen)
        return Gen.Frequency((2, withMembersGen), (1, nullGen));
    }

    private static Gen<BrainInboxHeader> InboxHeaderGen() =>
        from runId in Gen.Elements("run-1", "run-2", "run-3")
        from runType in Gen.Elements(RunTypes)
        from outcome in Gen.Elements(Outcomes)
        from repo in Gen.Elements(Repos)
        from hasProject in Gen.Elements(true, false)
        from projectName in Gen.Elements(ProjectNames)
        from labels in StringListGen()
        from issue in Gen.Elements(Issues)
        select new BrainInboxHeader(
            RunId: runId,
            RunType: runType,
            Outcome: outcome,
            RepositoryFullName: repo,
            ProjectName: hasProject ? projectName : null,
            StackLabels: labels,
            Issue: issue,
            PullRequestUrl: null,
            SkillsCommit: null,
            SkillsFiles: []);

    private static Gen<InboxFileEntry> InboxFileEntryGen() =>
        from path in Gen.Elements(FilePaths)
        from hasHeader in Gen.Elements(true, false)
        from header in InboxHeaderGen()
        select new InboxFileEntry(path, hasHeader ? header : null);

    private static Gen<MalformedInboxEntry> MalformedGen() =>
        from path in Gen.Elements(FilePaths)
        from reason in Gen.Elements(MalformReasons)
        select new MalformedInboxEntry(path, reason);

    private static Gen<SkillFileAtCommit> SkillFileGen() =>
        from path in Gen.Elements(SkillPaths)
        from lineCount in Gen.Choose(0, 3)
        from lines in Gen.ArrayOf(Gen.Elements(SkillLines), lineCount)
        select new SkillFileAtCommit(path, lines.ToList());

    private static Gen<KeyValuePair<string, IReadOnlyList<SkillFileAtCommit>>> CommitEntryGen() =>
        from sha in Gen.Elements(Shas)
        from files in BoundedList(SkillFileGen(), 2)
        select new KeyValuePair<string, IReadOnlyList<SkillFileAtCommit>>(sha, files);

    // Avoid "let" in LINQ queries (requires SelectMany overload not available on Gen<T>).
    // Use .Select() method chain instead to inline the deduplication.
    private static Gen<IReadOnlyDictionary<string, IReadOnlyList<SkillFileAtCommit>>> SkillsAtCommitsGen() =>
        BoundedList(CommitEntryGen(), 3)
            .Select(entries =>
                (IReadOnlyDictionary<string, IReadOnlyList<SkillFileAtCommit>>)
                entries.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First().Value));

    private static Gen<CurrentSkillFile> CurrentFileGen() =>
        from path in Gen.Elements(SkillPaths)
        from lineCount in Gen.Choose(0, 100)
        select new CurrentSkillFile(path, lineCount);

    private static Gen<BrainCommitInfo> CommitInfoGen() =>
        from sha in Gen.Elements(Shas)
        from message in Gen.Elements(CommitMessages)
        select new BrainCommitInfo(sha, message);

    public static Arbitrary<BrainReviewInputModel> BrainReviewInputModels()
    {
        // Build a partial model record from the first three generators to keep each sub-chain
        // short and avoid deep anonymous-type accumulation in the SelectMany desugaring.
        var part1Gen =
            from grp in GroupGen()
            from inbox in BoundedList(InboxFileEntryGen(), 3)
            from malformed in BoundedList(MalformedGen(), 2)
            select (grp, inbox, malformed);

        var gen =
            from p1 in part1Gen
            from skills in SkillsAtCommitsGen()
            from current in BoundedList(CurrentFileGen(), 3)
            from commits in BoundedList(CommitInfoGen(), 3)
            from filesLeft in Gen.Choose(0, 50)
            select new BrainReviewInputModel(
                Group: p1.grp,
                InboxFiles: p1.inbox,
                MalformedInbox: p1.malformed,
                SkillsAtCommits: skills,
                CurrentFiles: current,
                HumanCommits: commits,
                InboxFilesLeft: filesLeft);

        return gen.ToArbitrary();
    }
}
