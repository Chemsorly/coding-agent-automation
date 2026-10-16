using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="RefactoringProposalValidator"/>: the deterministic checks a proposal must
/// pass before the refactoring scan files it as an issue.
/// </summary>
public class RefactoringProposalValidatorTests : IDisposable
{
    private readonly string _workspace;

    public RefactoringProposalValidatorTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"proposal-validator-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        RefactoringTestWorkspace.CreateFile(_workspace, "src/A.cs");
        RefactoringTestWorkspace.CreateFile(_workspace, "src/B.cs");
        RefactoringTestWorkspace.CreateFile(_workspace, ".agent/refactoring-conventions.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private static RefactoringProposal Proposal(
        string title = "Extract shared validation", IReadOnlyList<string>? files = null, string? category = null) => new()
        {
            Title = title,
            AffectedFiles = files ?? ["src/A.cs"],
            Description = "desc",
            Rationale = "why",
            Category = category
        };

    private (IReadOnlyList<RefactoringProposal> Valid, IReadOnlyList<RefactoringProposalValidator.Rejection> Rejected) Validate(
        IReadOnlyList<RefactoringProposal> proposals, params string[] existingTitles) =>
        RefactoringProposalValidator.Validate(proposals, _workspace, existingTitles);

    [Fact]
    public void Validate_ValidProposal_IsKept()
    {
        var (valid, rejected) = Validate([Proposal(category: RefactoringCategories.Duplication)]);

        valid.Should().ContainSingle();
        rejected.Should().BeEmpty();
    }

    [Theory]
    [InlineData(".agent/refactoring-conventions.json")]
    [InlineData("./.agent/refactoring-conventions.json")]
    [InlineData(".brain/projects/x/SKILL.md")]
    [InlineData(".git/config")]
    public void Validate_AffectedFileInPipelineScratchSpace_IsRejected(string excludedPath)
    {
        // The scan once filed an issue to edit its own gitignored conventions file
        var (valid, rejected) = Validate([Proposal(files: ["src/A.cs", excludedPath])]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Contain("pipeline scratch space");
    }

    [Fact]
    public void Validate_NoAffectedFileExists_IsRejected()
    {
        var (valid, rejected) = Validate([Proposal(files: ["src/Missing.cs", "src/AlsoMissing.cs"])]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Contain("none of the affected files exist");
    }

    [Fact]
    public void Validate_SomeAffectedFilesAreNew_IsKept()
    {
        // A proposal may name a file it creates (e.g. a new test file) next to existing ones
        var (valid, _) = Validate([Proposal(files: ["src/A.cs", "tests/NewTests.cs"])]);

        valid.Should().ContainSingle();
    }

    [Fact]
    public void Validate_PathEscapingTheWorkspace_DoesNotCountAsExisting()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.cs");
        File.WriteAllText(outside, "// outside");
        try
        {
            var (valid, rejected) = Validate([Proposal(files: [$"../{Path.GetFileName(outside)}"])]);

            valid.Should().BeEmpty();
            rejected.Should().ContainSingle();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Validate_MoreAffectedFilesThanTheLimit_IsRejected()
    {
        var files = Enumerable.Range(0, RefactoringProposalValidator.MaxAffectedFiles + 1)
            .Select(i => $"src/File{i}.cs").Prepend("src/A.cs").ToList();

        var (valid, rejected) = Validate([Proposal(files: files)]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Contain("exceed the limit");
    }

    [Fact]
    public void Validate_NoAffectedFiles_IsRejected()
    {
        var (valid, rejected) = Validate([Proposal(files: [])]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Be("no affected files");
    }

    [Fact]
    public void Validate_UnknownCategory_IsRejected()
    {
        var (valid, rejected) = Validate([Proposal(category: "refactoring")]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Contain("unknown category 'refactoring'");
    }

    [Fact]
    public void Validate_CategoryIsCaseInsensitive()
    {
        var (valid, _) = Validate([Proposal(category: "Dead-Code")]);

        valid.Should().ContainSingle();
    }

    [Fact]
    public void Validate_TitleDuplicatesExistingIssue_IsRejected()
    {
        var (valid, rejected) = Validate(
            [Proposal(title: "Extract shared validation!")],
            "extract  shared   validation");

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Contain("duplicates");
    }

    [Fact]
    public void Validate_TitleDuplicatesEarlierProposalInBatch_RejectsOnlyTheLaterOne()
    {
        var first = Proposal(title: "Remove dead helper", files: ["src/A.cs"]);
        var second = Proposal(title: "Remove dead helper", files: ["src/B.cs"]);

        var (valid, rejected) = Validate([first, second]);

        valid.Should().ContainSingle().Which.Should().BeSameAs(first);
        rejected.Should().ContainSingle().Which.Proposal.Should().BeSameAs(second);
    }

    [Fact]
    public void Validate_EmptyTitle_IsRejected()
    {
        var (valid, rejected) = Validate([Proposal(title: "  ")]);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle().Which.Reason.Should().Be("empty title");
    }

    [Fact]
    public void Validate_KeepsInputOrderOfValidProposals()
    {
        var a = Proposal(title: "First", files: ["src/A.cs"]);
        var bad = Proposal(title: "Bad", files: ["src/Missing.cs"]);
        var b = Proposal(title: "Second", files: ["src/B.cs"]);

        var (valid, _) = Validate([a, bad, b]);

        valid.Should().Equal(a, b);
    }

    [Theory]
    [InlineData("Extract  Shared-Validation!", "extract shared validation")]
    [InlineData("  #12: Fix `Foo`  ", "12 fix foo")]
    public void NormalizeTitle_KeepsLowercaseWordsOnly(string title, string expected)
    {
        RefactoringProposalValidator.NormalizeTitle(title).Should().Be(expected);
    }

    [Fact]
    public void Validate_TestGapCategory_IsAccepted()
    {
        // test-gap is in RefactoringCategories.All; the validator must accept it
        var proposal = Proposal(category: RefactoringCategories.TestGap);
        var (valid, rejected) = Validate([proposal]);

        valid.Should().ContainSingle();
        rejected.Should().BeEmpty();
    }

    [Fact]
    public void Validate_CategoryOutsideAllowedCategories_IsRejected()
    {
        // When allowedCategories is supplied, proposals with a category outside it must be rejected
        var proposal = Proposal(category: RefactoringCategories.Bug);
        var allowedCategories = RefactoringCategories.Structural;

        var (valid, rejected) = RefactoringProposalValidator.Validate(
            [proposal], _workspace, [], allowedCategories);

        valid.Should().BeEmpty();
        rejected.Should().ContainSingle()
            .Which.Reason.Should().Be("category 'bug' is outside the scan focus");
    }

    // TODO [WARNING]: Add a test verifying that a proposal with a null Category still passes when
    // allowedCategories is non-null. The production guard is `if (proposal.Category is not null &&
    // allowedCategories is not null && ...)`, so removing the null check would cause a NullReferenceException
    // on the .Trim() call — but no test currently catches this regression path.
    // Test name: Validate_NullCategory_PassesEvenWhenAllowedCategoriesIsSet
    // Arrange: Proposal(category: null), allowedCategories = RefactoringCategories.Structural
    // Assert: valid.ContainSingle(), rejected.BeEmpty()
}
