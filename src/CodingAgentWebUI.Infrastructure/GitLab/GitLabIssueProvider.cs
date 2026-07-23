using NGitLab;
using NGitLab.Models;
using Serilog;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using PipelineIssueComment = CodingAgentWebUI.Pipeline.Models.IssueComment;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// GitLab implementation of <see cref="IIssueProvider"/> using the NGitLab library.
/// Maps GitLab issue operations (CRUD, labels, comments) to the pipeline's provider interface.
/// Uses IID-based addressing (project-scoped identifiers) for all issue operations.
/// </summary>
public class GitLabIssueProvider : GitLabProviderBase, IIssueProvider
{
    public IssueProviderType ProviderType => IssueProviderType.GitLab;

    /// <summary>
    /// Creates a provider with a static access token.
    /// </summary>
    public GitLabIssueProvider(string apiUrl, string accessToken, int projectId)
        : base(apiUrl, accessToken, projectId) { }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// </summary>
    public GitLabIssueProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, int projectId)
        : base(apiUrl, tokenProvider, projectId) { }

    /// <summary>
    /// Internal constructor for testing with a mock IGitLabClient.
    /// </summary>
    internal GitLabIssueProvider(IGitLabClient client, int projectId)
        : base(client, projectId) { }

    /// <inheritdoc />
    public async Task<IssueDetail> GetIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var iid = ParseIdentifier(identifier);

        try
        {
            var issue = await ExecuteWithResilienceAsync(
                client => client.Issues.GetAsync(ProjectId, iid, ct),
                "GetIssue", ct);

            return MapToIssueDetail(issue);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            Log.Warning("Issue with identifier '{Identifier}' not found in project {ProjectId}", identifier, ProjectId);
            throw new InvalidOperationException(
                $"Issue with identifier '{identifier}' was not found in project {ProjectId}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        ValidatePaginationParameters(page, pageSize);

        var client = await GetClientAsync(ct);
        var query = new IssueQuery
        {
            State = IssueState.opened,
            PerPage = pageSize,
            Labels = labels is { Count: > 0 } ? string.Join(",", labels) : null
        };

        return await EnumerateIssuesAsync(client, query, page, pageSize, ct);
    }

    /// <inheritdoc />
    public Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, CancellationToken ct)
        => ListOpenIssuesAsync(page, pageSize, labels: null, ct);

    /// <inheritdoc />
    public async Task<PagedResult<IssueSummary>> ListClosedIssuesAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
    {
        ValidatePaginationParameters(page, pageSize);

        var client = await GetClientAsync(ct);
        var query = new IssueQuery
        {
            State = IssueState.closed,
            PerPage = pageSize,
            Labels = labels is { Count: > 0 } ? string.Join(",", labels) : null,
            UpdatedAfter = since?.ToUniversalTime()
        };

        return await EnumerateIssuesAsync(client, query, page, pageSize, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineIssueComment>> ListCommentsAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var iid = ParseIdentifier(identifier);

        var notes = await ExecuteWithResilienceAsync(
            client =>
            {
                var noteClient = client.GetProjectIssueNoteClient(ProjectId);
                return Task.Run(() => noteClient.ForIssue(iid).ToList(), ct);
            },
            "ListComments", ct);

        return notes
            .Select(n => new PipelineIssueComment
            {
                Id = n.NoteId.ToString(),
                Body = n.Body ?? string.Empty,
                Author = n.Author?.Username ?? string.Empty,
                CreatedAt = n.CreatedAt.ToUniversalTime()
            })
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<string?> PostCommentAsync(IssueIdentifier identifier, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(body);
        var iid = ParseIdentifier(identifier);

        try
        {
            var note = await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var noteClient = client.GetProjectIssueNoteClient(ProjectId);
                    return Task.Run(() => noteClient.Create(new ProjectIssueNoteCreate { IssueId = iid, Body = body }), ct);
                },
                "PostComment", ct);

            // Construct the note URL from instance base + project path + issue IID + note ID
            if (note is not null && PathWithNamespace is not null)
                return $"{ApiUrl.TrimEnd('/')}/{PathWithNamespace}/-/issues/{iid}#note_{note.NoteId}";
            return null;
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            Log.Warning("Issue with identifier '{Identifier}' not found in project {ProjectId}", identifier, ProjectId);
            throw new InvalidOperationException(
                $"Issue with identifier '{identifier}' was not found in project {ProjectId}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task UpdateCommentAsync(IssueIdentifier issueIdentifier, string commentId, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);
        var iid = ParseIdentifier(issueIdentifier);

        if (!long.TryParse(commentId, out var noteId))
        {
            Log.Warning("Invalid comment identifier '{CommentId}' — expected numeric note ID", commentId);
            throw new ArgumentException(
                $"Invalid comment identifier: '{commentId}'. Expected a numeric note ID.", nameof(commentId));
        }

        try
        {
            await ExecuteWriteWithResilienceAsync(
                client =>
                {
                    var noteClient = client.GetProjectIssueNoteClient(ProjectId);
                    return Task.Run(() => noteClient.Edit(new ProjectIssueNoteEdit { NoteId = noteId, IssueId = iid, Body = body }), ct);
                },
                "UpdateComment", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            // Could be either issue not found or note not found
            var message = ex.Message?.Contains("note", StringComparison.OrdinalIgnoreCase) == true
                ? $"Comment '{commentId}' was not found on issue '{issueIdentifier}' in project {ProjectId}."
                : $"Issue with identifier '{issueIdentifier}' was not found in project {ProjectId}.";
            Log.Error(ex, "Failed to update comment on issue '{Identifier}' in project {ProjectId}: {Message}", issueIdentifier, ProjectId, message);
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <inheritdoc />
    public async Task AddLabelsAsync(IssueIdentifier identifier, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(labels);
        var iid = ParseIdentifier(identifier);

        // Ensure project-level labels exist before adding to issue
        await EnsureProjectLabelsExistAsync(labels, ct);

        // Get current issue labels and merge with new ones
        var issue = await ExecuteWithResilienceAsync(
            client => client.Issues.GetAsync(ProjectId, iid, ct),
            "GetIssueForLabels", ct);

        var currentLabels = issue.Labels ?? [];
        var mergedLabels = currentLabels.Union(labels, StringComparer.OrdinalIgnoreCase).ToArray();

        await ExecuteWriteWithResilienceAsync(
            client => client.Issues.EditAsync(new IssueEdit
            {
                ProjectId = ProjectId,
                IssueId = iid,
                Labels = string.Join(",", mergedLabels)
            }, ct),
            "AddLabels", ct);
    }

    /// <inheritdoc />
    public async Task RemoveLabelAsync(IssueIdentifier identifier, string label, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(label);
        var iid = ParseIdentifier(identifier);

        try
        {
            var issue = await ExecuteWithResilienceAsync(
                client => client.Issues.GetAsync(ProjectId, iid, ct),
                "GetIssueForRemoveLabel", ct);

            var currentLabels = issue.Labels ?? [];
            if (!currentLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                return; // Label not present — no-op

            var updatedLabels = currentLabels
                .Where(l => !l.Equals(label, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            await ExecuteWriteWithResilienceAsync(
                client => client.Issues.EditAsync(new IssueEdit
                {
                    ProjectId = ProjectId,
                    IssueId = iid,
                    Labels = string.Join(",", updatedLabels)
                }, ct),
                "RemoveLabel", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            // Issue not found — treat as no-op
        }
    }

    /// <inheritdoc />
    public async Task CloseIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var iid = ParseIdentifier(identifier);

        try
        {
            var issue = await ExecuteWithResilienceAsync(
                client => client.Issues.GetAsync(ProjectId, iid, ct),
                "GetIssueForClose", ct);

            // No-op if already closed
            if (string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase))
                return;

            await ExecuteWriteWithResilienceAsync(
                client => client.Issues.EditAsync(new IssueEdit
                {
                    ProjectId = ProjectId,
                    IssueId = iid,
                    State = "close"
                }, ct),
                "CloseIssue", ct);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            Log.Warning("Issue with identifier '{Identifier}' not found in project {ProjectId}", identifier, ProjectId);
            throw new InvalidOperationException(
                $"Issue with identifier '{identifier}' was not found in project {ProjectId}.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<CreatedIssueResult> CreateIssueAsync(
        string title, string body, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Issue title cannot be null or empty.", nameof(title));
        ArgumentNullException.ThrowIfNull(body);

        var issueCreate = new IssueCreate
        {
            ProjectId = ProjectId,
            Title = title,
            Description = body,
            Labels = labels is { Count: > 0 } ? string.Join(",", labels) : null
        };

        var created = await ExecuteWriteWithResilienceAsync(
            client => client.Issues.CreateAsync(issueCreate, ct),
            "CreateIssue", ct);

        return new CreatedIssueResult
        {
            Identifier = created.IssueId.ToString(),
            Url = created.WebUrl ?? string.Empty
        };
    }

    /// <inheritdoc />
    public async Task<bool> IsIssueClosedAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        var iid = ParseIdentifier(identifier);

        try
        {
            var issue = await ExecuteWithResilienceAsync(
                client => client.Issues.GetAsync(ProjectId, iid, ct),
                "IsIssueClosed", ct);

            return string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            Log.Warning("Issue #{IssueIid} not found when checking state in project {ProjectId}", iid, ProjectId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListRepositoryLabelsAsync(CancellationToken ct)
    {
        var projectLabels = await ExecuteWithResilienceAsync(
            client => Task.Run(() => client.Labels.ForProject(ProjectId).ToList(), ct),
            "ListRepositoryLabels", ct);
        return projectLabels.Select(l => l.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> HasAgentLabelsAsync(CancellationToken ct)
    {
        var projectLabels = await ExecuteWithResilienceAsync(
            client => Task.Run(() => client.Labels.ForProject(ProjectId).ToList(), ct),
            "HasAgentLabels", ct);

        var labelNames = projectLabels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AgentLabels.All.All(name => labelNames.Contains(name));
    }

    /// <inheritdoc />
    public async Task<bool> EnsureAgentLabelsAsync(CancellationToken ct)
    {
        var allSucceeded = true;

        foreach (var (name, color) in AgentLabels.Definitions)
        {
            try
            {
                await ExecuteWriteWithResilienceAsync(
                    client => Task.Run(
                        () => client.Labels.CreateProjectLabel(ProjectId, new ProjectLabelCreate { Name = name, Color = $"#{color}" }),
                        ct),
                    "EnsureAgentLabels", ct);
            }
            catch (GitLabException ex) when ((int)ex.StatusCode == 409 || (int)ex.StatusCode == 400)
            {
                // Label already exists — skip (409 Conflict or 400 with "already exists" message)
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning(ex, "Failed to create agent label '{LabelName}' in project {ProjectId}", name, ProjectId);
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    #region Private Helpers

    /// <summary>
    /// Validates pagination parameters (page >= 1, pageSize 1–100).
    /// </summary>
    private static void ValidatePaginationParameters(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
    }

    /// <summary>
    /// Enumerates issues from the NGitLab async enumerable using the overfetch-by-one pattern
    /// to determine HasMore without relying on page metadata (which NGitLab doesn't expose).
    /// </summary>
    private async Task<PagedResult<IssueSummary>> EnumerateIssuesAsync(
        IGitLabClient client, IssueQuery query, int page, int pageSize, CancellationToken ct)
    {
        var allItems = new List<Issue>();
        var skipCount = (page - 1) * pageSize;

        await foreach (var issue in client.Issues.ForProjectAsync(ProjectId, query).WithCancellation(ct))
        {
            if (skipCount > 0)
            {
                skipCount--;
                continue;
            }

            allItems.Add(issue);
            if (allItems.Count > pageSize)
                break; // Got enough to determine HasMore
        }

        var hasMore = allItems.Count > pageSize;
        var items = allItems.Take(pageSize).Select(MapToIssueSummary).ToList();

        return new PagedResult<IssueSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    /// <summary>
    /// Ensures that the specified labels exist as project-level labels.
    /// Creates any that are missing (idempotent).
    /// </summary>
    private async Task EnsureProjectLabelsExistAsync(IReadOnlyList<string> labels, CancellationToken ct)
    {
        foreach (var label in labels)
        {
            try
            {
                await ExecuteWriteWithResilienceAsync(
                    client => Task.Run(
                        () => client.Labels.CreateProjectLabel(ProjectId, new ProjectLabelCreate { Name = label, Color = "#428BCA" }),
                        ct),
                    "EnsureProjectLabel", ct);
            }
            catch (GitLabException ex) when ((int)ex.StatusCode == 409 || (int)ex.StatusCode == 400)
            {
                // Label already exists — skip
            }
        }
    }

    /// <summary>
    /// Maps a GitLab <see cref="Issue"/> to an <see cref="IssueDetail"/>.
    /// </summary>
    private static IssueDetail MapToIssueDetail(Issue issue)
    {
        return new IssueDetail
        {
            Identifier = issue.IssueId.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Description ?? string.Empty,
            Labels = issue.Labels?.ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>()
        };
    }

    /// <summary>
    /// Maps a GitLab <see cref="Issue"/> to an <see cref="IssueSummary"/>.
    /// </summary>
    private static IssueSummary MapToIssueSummary(Issue issue)
    {
        return new IssueSummary
        {
            Identifier = issue.IssueId.ToString(),
            Title = issue.Title ?? string.Empty,
            Description = issue.Description,
            Labels = issue.Labels?.ToList().AsReadOnly()
                ?? (IReadOnlyList<string>)Array.Empty<string>(),
            CreatedAt = issue.CreatedAt.ToUniversalTime()
        };
    }

    #endregion
}
