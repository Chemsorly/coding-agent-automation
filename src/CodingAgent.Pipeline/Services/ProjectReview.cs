using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Adds a project's reviewers to the reviewers of a code review (see <see cref="PipelineProject.ProjectReviewEnabled"/>).
/// They run with the others, and only they are told about the project's other repositories.
/// </summary>
public static class ProjectReview
{
    /// <summary>The id of the reviewer configuration that holds the project's reviewers.</summary>
    public const string ConfigurationId = "project-review";

    /// <summary>
    /// <paramref name="reviewers"/> plus one configuration with <paramref name="projectReviewers"/>. Each project
    /// reviewer's instructions end with the list of the project's other repositories, and a name another reviewer
    /// already has gets a number, as each reviewer writes its findings to a file named after it.
    /// </summary>
    public static IReadOnlyList<ReviewerConfiguration> AddReviewers(
        IReadOnlyList<ReviewerConfiguration> reviewers,
        IReadOnlyList<ReviewAgent> projectReviewers,
        IReadOnlyList<RepositoryTarget>? repositories)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        ArgumentNullException.ThrowIfNull(projectReviewers);
        if (projectReviewers.Count == 0)
            return reviewers;

        var names = reviewers.SelectMany(r => r.Agents).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repositoryList = RepositoryList(repositories);
        var agents = projectReviewers
            .Select(r => new ReviewAgent { Name = UniqueName(r.Name, names), Prompt = $"{r.Prompt}\n\n{repositoryList}" })
            .ToList();

        return
        [
            .. reviewers,
            new ReviewerConfiguration
            {
                Id = ConfigurationId,
                DisplayName = "Project Review",
                Agents = agents,
                Enabled = true,
                ExecutionOrder = int.MaxValue
            }
        ];
    }

    /// <summary>The "Project repositories" section of a project reviewer's instructions.</summary>
    internal static string RepositoryList(IReadOnlyList<RepositoryTarget>? repositories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Project repositories");
        sb.AppendLine();
        if (repositories is not { Count: > 0 })
        {
            sb.Append("This project has no other repositories: check the change against the project's decisions and documentation.");
            return sb.ToString();
        }

        sb.AppendLine("This repository is the workspace root. The project's other repositories, read-only on their default branches:");
        foreach (var repository in repositories)
        {
            sb.AppendLine(repository.LocalPath is { } path
                ? $"- `{repository.TemplateName}`: `{path}/`"
                : $"- `{repository.TemplateName}`: not available, it could not be cloned");
        }

        return sb.ToString().TrimEnd();
    }

    private static string UniqueName(string name, HashSet<string> names)
    {
        var candidate = name;
        for (var suffix = 2; !names.Add(candidate); suffix++)
            candidate = $"{name}{suffix}";
        return candidate;
    }
}
