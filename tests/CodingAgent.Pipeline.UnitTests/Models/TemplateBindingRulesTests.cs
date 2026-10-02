using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="TemplateBindingRules"/>: among enabled templates, a repository and an issue tracker
/// each belong to one template, and a name is unique within its project.
/// </summary>
public class TemplateBindingRulesTests
{
    private const string ProjectA = "project-a";
    private const string ProjectB = "project-b";

    private static PipelineJobTemplate Template(
        string id, string name, string issue, string repo, bool enabled = true) => new()
    {
        Id = id, Name = name, IssueProviderId = issue, RepoProviderId = repo, Enabled = enabled
    };

    private static PipelineProject Project(string id, params string[] templateIds) => new()
    {
        Id = id, Name = id, TemplateIds = templateIds
    };

    [Fact]
    public void Validate_UniqueTemplate_IsAccepted()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", "Web", "issues-2", "repo-2");

        TemplateBindingRules.Validate(candidate, ProjectA, [existing], [Project(ProjectA, "t1")])
            .Should().BeNull();
    }

    [Fact]
    public void Validate_RepositoryOfAnotherEnabledTemplate_IsRefused()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", "Web", "issues-2", "repo-1");

        TemplateBindingRules.Validate(candidate, ProjectB, [existing], [Project(ProjectA, "t1"), Project(ProjectB)])
            .Should().Contain("repository").And.Contain("\"Api\"");
    }

    [Fact]
    public void Validate_TrackerOfAnotherEnabledTemplate_IsRefused()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", "Web", "issues-1", "repo-2");

        TemplateBindingRules.Validate(candidate, ProjectB, [existing], [Project(ProjectA, "t1"), Project(ProjectB)])
            .Should().Contain("issue tracker").And.Contain("\"Api\"");
    }

    [Theory]
    [InlineData("Api")]
    [InlineData(" api ")]
    public void Validate_NameOfAnotherEnabledTemplateInTheSameProject_IsRefused(string name)
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", name, "issues-2", "repo-2");

        TemplateBindingRules.Validate(candidate, ProjectA, [existing], [Project(ProjectA, "t1")])
            .Should().Contain("unique within a project");
    }

    [Fact]
    public void Validate_SameNameInAnotherProject_IsAccepted()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", "Api", "issues-2", "repo-2");

        TemplateBindingRules.Validate(candidate, ProjectB, [existing], [Project(ProjectA, "t1"), Project(ProjectB)])
            .Should().BeNull();
    }

    [Fact]
    public void Validate_DisabledCandidate_IsAccepted_SoAConflictCanBeSwitchedOff()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1");
        var candidate = Template("t2", "Api", "issues-1", "repo-1", enabled: false);

        TemplateBindingRules.Validate(candidate, ProjectA, [existing, candidate], [Project(ProjectA, "t1", "t2")])
            .Should().BeNull();
    }

    [Fact]
    public void Validate_ConflictWithADisabledTemplate_IsAccepted()
    {
        var existing = Template("t1", "Api", "issues-1", "repo-1", enabled: false);
        var candidate = Template("t2", "Api", "issues-1", "repo-1");

        TemplateBindingRules.Validate(candidate, ProjectA, [existing], [Project(ProjectA, "t1")])
            .Should().BeNull();
    }

    [Fact]
    public void Validate_TheTemplateItself_IsNotAConflict()
    {
        var saved = Template("t1", "Api", "issues-1", "repo-1");
        var edited = saved with { ReviewEnabled = false };

        TemplateBindingRules.Validate(edited, ProjectA, [saved], [Project(ProjectA, "t1")])
            .Should().BeNull();
    }

    [Fact]
    public void Validate_EditedNameBrainAndCi_AreAccepted()
    {
        var saved = Template("t1", "Api", "issues-1", "repo-1");
        var edited = saved with { Name = "Api v2", BrainProviderId = "brain-1", BrainReadOnly = true, PipelineProviderId = "ci-1" };

        TemplateBindingRules.Validate(edited, ProjectA, [saved], [Project(ProjectA, "t1")])
            .Should().BeNull();
    }

    [Theory]
    [InlineData("issues-2", "repo-1")]
    [InlineData("issues-1", "repo-2")]
    public void Validate_ChangedRepositoryOrTrackerOfASavedTemplate_IsRefused(string issue, string repo)
    {
        var saved = Template("t1", "Api", "issues-1", "repo-1", enabled: false);
        var edited = saved with { IssueProviderId = issue, RepoProviderId = repo };

        TemplateBindingRules.Validate(edited, ProjectA, [saved], [Project(ProjectA, "t1")])
            .Should().Be("A template keeps its repository and issue tracker. To use another one, add a new template.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyName_IsRefused(string name)
    {
        TemplateBindingRules.Validate(Template("t1", name, "issues-1", "repo-1"), ProjectA, [], [Project(ProjectA)])
            .Should().Be("A template needs a name.");
    }

    [Fact]
    public void FindConflicts_ReportsEachGroupOfEnabledTemplates()
    {
        var templates = new[]
        {
            Template("t1", "Api", "issues-1", "repo-1"),
            Template("t2", "Api copy", "issues-2", "repo-1"),
            Template("t3", "Web", "issues-3", "repo-3"),
            Template("t4", "web", "issues-4", "repo-4"),
            Template("t5", "Off", "issues-3", "repo-5", enabled: false),
        };
        var projects = new[] { Project(ProjectA, "t1", "t3", "t4"), Project(ProjectB, "t2", "t5") };

        var conflicts = TemplateBindingRules.FindConflicts(templates, projects);

        conflicts.Should().HaveCount(2);
        conflicts.Should().Contain(c => c.Contains("\"Api\"") && c.Contains("\"Api copy\"") && c.Contains("same repository"));
        conflicts.Should().Contain(c => c.Contains("\"Web\"") && c.Contains("\"web\"") && c.Contains($"project \"{ProjectA}\""));
    }

    [Fact]
    public void FindConflicts_NoConflicts_ReturnsEmpty()
    {
        var templates = new[]
        {
            Template("t1", "Api", "issues-1", "repo-1"),
            Template("t2", "Web", "issues-2", "repo-2"),
        };

        TemplateBindingRules.FindConflicts(templates, [Project(ProjectA, "t1", "t2")]).Should().BeEmpty();
    }

    [Fact]
    public void TemplateOrder_SortsByNameIgnoringCase_ThenExactName_ThenId()
    {
        var items = new[] { ("b", "beta"), ("a2", "alpha"), ("c", "Gamma"), ("a1", "Alpha"), ("a0", "alpha") };

        TemplateOrder.ByName(items, i => i.Item2, i => i.Item1).Select(i => i.Item1)
            .Should().Equal("a1", "a0", "a2", "b", "c");
    }
}
