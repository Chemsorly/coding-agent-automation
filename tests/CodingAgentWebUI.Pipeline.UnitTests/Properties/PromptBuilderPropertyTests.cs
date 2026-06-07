using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for BuildReworkPrompt in PromptBuilder.
/// Feature: 009-pr-rework-pipeline
/// </summary>
public class PromptBuilderPropertyTests
{
    /// <summary>
    /// Feature: 009-pr-rework-pipeline, Property 3: Conflict files appear in rework prompt
    /// 
    /// For any non-empty list of conflict file paths, BuildReworkPrompt returns a prompt
    /// containing a "Merge Conflicts" section that lists every file path from the input.
    /// 
    /// **Validates: Requirements REQ-8.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ReworkPromptArbitraries) })]
    public void BuildReworkPrompt_ConflictFilesAppearInOutput(NonEmptyFilePathList conflictFiles)
    {
        var files = conflictFiles.Paths;

        var result = PromptBuilder.BuildReworkPrompt(
            files,
            Array.Empty<PullRequestReviewComment>());

        result.Should().NotBeNull();
        result!.Should().Contain("## Merge Conflicts");

        foreach (var file in files)
        {
            result.Should().Contain(file);
        }
    }

    /// <summary>
    /// Feature: 009-pr-rework-pipeline, Property 4: Review comments appear in rework prompt
    /// 
    /// For any non-empty list of PullRequestReviewComment objects, BuildReworkPrompt returns
    /// a prompt containing a "Review Feedback" section that includes each comment's author,
    /// body, and file path (when present).
    /// 
    /// **Validates: Requirements REQ-8.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ReworkPromptArbitraries) })]
    public void BuildReworkPrompt_ReviewCommentsAppearInOutput(NonEmptyReviewCommentList commentList)
    {
        var comments = commentList.Comments;

        var result = PromptBuilder.BuildReworkPrompt(
            Array.Empty<string>(),
            comments);

        result.Should().NotBeNull();
        result!.Should().Contain("## Review Feedback");

        foreach (var comment in comments)
        {
            result.Should().Contain($"@{comment.Author}");
            result.Should().Contain(comment.Body);

            if (comment.Path != null)
            {
                result.Should().Contain(comment.Path);
            }
        }
    }

    // --- Custom wrapper types and Arbitraries for FsCheck ---

    /// <summary>Wrapper for a non-empty list of non-null/non-empty file paths.</summary>
    public sealed class NonEmptyFilePathList
    {
        public IReadOnlyList<string> Paths { get; }
        public NonEmptyFilePathList(IReadOnlyList<string> paths) => Paths = paths;
        public override string ToString() => $"[{string.Join(", ", Paths)}]";
    }

    /// <summary>Wrapper for a non-empty list of PullRequestReviewComment.</summary>
    public sealed class NonEmptyReviewCommentList
    {
        public IReadOnlyList<PullRequestReviewComment> Comments { get; }
        public NonEmptyReviewCommentList(IReadOnlyList<PullRequestReviewComment> comments) => Comments = comments;
        public override string ToString() => $"[{Comments.Count} comments]";
    }

    public class ReworkPromptArbitraries
    {
        private static readonly string[] Directories = { "src/", "tests/", "lib/", "docs/", "config/" };
        private static readonly string[] FileNames = { "App.cs", "Test.cs", "Utils.cs", "README.md", "Program.cs", "Startup.cs" };
        private static readonly string[] Authors = { "alice", "bob", "charlie", "reviewer1", "reviewer2" };
        private static readonly string[] FilePaths = { "src/App.cs", "tests/Test.cs", "lib/Utils.cs", "README.md" };

        public static Arbitrary<NonEmptyFilePathList> NonEmptyFilePathListArb()
        {
            var pathGen =
                from dir in Gen.Elements(Directories)
                from file in Gen.Elements(FileNames)
                select $"{dir}{file}";

            var listGen =
                from count in Gen.Choose(1, 10)
                from paths in Gen.ArrayOf(pathGen, count)
                let distinct = paths.Distinct().ToList()
                where distinct.Count > 0
                select new NonEmptyFilePathList(distinct.AsReadOnly());

            return listGen.ToArbitrary();
        }

        public static Arbitrary<NonEmptyReviewCommentList> NonEmptyReviewCommentListArb()
        {
            var commentGen =
                from id in Gen.Choose(1, 100000).Select(i => i.ToString())
                from body in Gen.Elements(
                    "Please fix the null check",
                    "This method needs error handling",
                    "Consider using async/await here",
                    "Missing unit test for edge case",
                    "Variable naming could be clearer")
                from author in Gen.Elements(Authors)
                from year in Gen.Choose(2020, 2026)
                from month in Gen.Choose(1, 12)
                from day in Gen.Choose(1, 28)
                from hasPath in Gen.Elements(true, false)
                from path in hasPath
                    ? Gen.Elements(FilePaths).Select(p => (string?)p)
                    : Gen.Constant((string?)null)
                select new PullRequestReviewComment
                {
                    Id = id,
                    Body = body,
                    Author = author,
                    CreatedAt = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc),
                    Path = path
                };

            var listGen =
                from count in Gen.Choose(1, 10)
                from comments in Gen.ArrayOf(commentGen, count)
                select new NonEmptyReviewCommentList(comments.ToList().AsReadOnly());

            return listGen.ToArbitrary();
        }
    }
}
