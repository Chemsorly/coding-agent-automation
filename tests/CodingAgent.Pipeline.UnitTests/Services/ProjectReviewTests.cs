using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// The project review: which reviewers a project adds (<see cref="PipelineProject.ActiveProjectReviewers"/>) and how
/// they join a code review's reviewers (<see cref="ProjectReview.AddReviewers"/>).
/// </summary>
public class ProjectReviewTests
{
    private static PipelineProject Project(bool enabled, params ReviewAgent[] reviewers) => new()
    {
        Id = "p1",
        Name = "Shop",
        ProjectReviewEnabled = enabled,
        ProjectReviewers = reviewers
    };

    private static ReviewerConfiguration LabelReviewers(params string[] names) => new()
    {
        Id = "default-reviewers",
        DisplayName = "Default Reviewers",
        Agents = names.Select(n => new ReviewAgent { Name = n, Prompt = $"{n} instructions" }).ToList()
    };

    // ── ActiveProjectReviewers ──────────────────────────────────────────────

    [Fact]
    public void ActiveProjectReviewers_ProjectReviewOff_IsEmptyEvenWithReviewers()
    {
        Project(enabled: false, new ReviewAgent { Name = "Product", Prompt = "Check it" })
            .ActiveProjectReviewers().Should().BeEmpty();
    }

    [Fact]
    public void ActiveProjectReviewers_OnWithoutReviewers_IsOneDefaultReviewer()
    {
        var reviewer = Project(enabled: true).ActiveProjectReviewers().Should().ContainSingle().Subject;

        reviewer.Name.Should().Be(PipelineConfigurationDefaults.DefaultProjectReviewerName);
        reviewer.Prompt.Should().Be(PipelineConfigurationDefaults.DefaultProjectReviewPrompt);
    }

    [Fact]
    public void ActiveProjectReviewers_EmptyNameOrPrompt_GetsTheDefaultAndOthersAreKeptInOrder()
    {
        var reviewers = Project(enabled: true,
                new ReviewAgent { Name = " ", Prompt = "" },
                new ReviewAgent { Name = "Contracts", Prompt = "Check the API contracts" })
            .ActiveProjectReviewers();

        reviewers.Select(r => (r.Name, r.Prompt)).Should().Equal(
            (PipelineConfigurationDefaults.DefaultProjectReviewerName, PipelineConfigurationDefaults.DefaultProjectReviewPrompt),
            ("Contracts", "Check the API contracts"));
    }

    [Fact]
    public void DefaultProjectReviewPrompt_AsksForCurrentSourcesTheBrainAndTheMcpServers()
    {
        var prompt = PipelineConfigurationDefaults.DefaultProjectReviewPrompt;

        prompt.Should().Contain("Project repositories", "the repository list is appended under that heading")
            .And.Contain("may span several repositories", "a project can have a single repository")
            .And.Contain("`.brain/`")
            .And.Contain("One brain can serve several projects", "a lesson may be about another project")
            .And.Contain("MCP servers")
            .And.Contain("git log -1 --format=%cs")
            .And.Contain("[CRITICAL]").And.Contain("[WARNING]").And.Contain("[SUGGESTION]");
    }

    // ── AddReviewers ────────────────────────────────────────────────────────

    [Fact]
    public void AddReviewers_NoProjectReviewers_ReturnsTheReviewersUnchanged()
    {
        IReadOnlyList<ReviewerConfiguration> reviewers = [LabelReviewers("Correctness")];

        ProjectReview.AddReviewers(reviewers, [], repositories: null).Should().BeSameAs(reviewers);
    }

    [Fact]
    public void AddReviewers_AddsTheProjectReviewersLastWithTheRepositoryListAtTheEndOfTheirInstructions()
    {
        IReadOnlyList<ReviewerConfiguration> reviewers = [LabelReviewers("Correctness", "SecurityReviewer")];
        RepositoryTarget[] repositories =
        [
            new() { TemplateName = "api", Description = "", RepoProviderId = "repo-api", LocalPath = ".agent/project-repos/api" },
            new() { TemplateName = "mobile", Description = "", RepoProviderId = "repo-mobile" }
        ];

        var result = ProjectReview.AddReviewers(
            reviewers, [new ReviewAgent { Name = "ProjectReviewer", Prompt = "Check the project." }], repositories);

        result.Should().HaveCount(2);
        result[0].Should().BeSameAs(reviewers[0]);
        var project = result[1];
        project.Id.Should().Be(ProjectReview.ConfigurationId);
        project.Enabled.Should().BeTrue();
        var reviewer = project.Agents.Should().ContainSingle().Subject;
        reviewer.Name.Should().Be("ProjectReviewer");
        reviewer.Prompt.Should().StartWith("Check the project.\n\n## Project repositories")
            .And.Contain("- `api`: `.agent/project-repos/api/`")
            .And.Contain("- `mobile`: not available, it could not be cloned");
        ReviewerResolver.FlattenAgents(result).Select(a => a.Name)
            .Should().Equal("Correctness", "SecurityReviewer", "ProjectReviewer");
    }

    [Fact]
    public void AddReviewers_NameAnotherReviewerHas_GetsANumber()
    {
        // Each reviewer writes its findings to a file named after it, so two reviewers must not share a name
        var result = ProjectReview.AddReviewers(
            [LabelReviewers("Correctness")],
            [new ReviewAgent { Name = "correctness", Prompt = "a" }, new ReviewAgent { Name = "correctness", Prompt = "b" }],
            repositories: null);

        ReviewerResolver.FlattenAgents(result).Select(a => a.Name)
            .Should().Equal("Correctness", "correctness2", "correctness3");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryList_NoOtherRepository_SaysSo(bool emptyList)
    {
        ProjectReview.RepositoryList(emptyList ? [] : null)
            .Should().StartWith("## Project repositories")
            .And.Contain("This project has no other repositories");
    }
}
