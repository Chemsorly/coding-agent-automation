using Octokit;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Lightweight helper for validating GitHub tokens and listing accessible repositories.
/// Used by the Settings page for provider configuration and validation.
/// </summary>
public class GitHubValidationService
{
    /// <summary>
    /// Validates a GitHub token by attempting to get the authenticated user.
    /// Returns the username on success, or an error message on failure.
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateTokenAsync(
        string apiUrl, string token, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(apiUrl, token);
            var user = await client.User.Current();
            return (true, $"Authenticated as {user.Login}");
        }
        catch (AuthorizationException)
        {
            return (false, "Invalid token or token has expired");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that the token has access to a specific repository.
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateRepoAccessAsync(
        string apiUrl, string token, string owner, string repo, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(apiUrl, token);
            var repository = await client.Repository.Get(owner, repo);
            var permissions = repository.Permissions;

            var permList = new List<string>();
            if (permissions.Pull) permList.Add("read");
            if (permissions.Push) permList.Add("write");
            if (permissions.Admin) permList.Add("admin");
            var permSummary = permList.Count > 0 ? string.Join(", ", permList) : "none";

            return (true, $"✅ {repository.FullName} — permissions: {permSummary}");
        }
        catch (NotFoundException)
        {
            return (false, $"Repository {owner}/{repo} not found or token lacks access");
        }
        catch (AuthorizationException)
        {
            return (false, "Invalid token or token has expired");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists repositories accessible to the authenticated token.
    /// Returns up to 100 repos sorted by most recently pushed.
    /// </summary>
    public async Task<IReadOnlyList<(string FullName, string Owner, string Name)>> ListRepositoriesAsync(
        string apiUrl, string token, CancellationToken ct)
    {
        try
        {
            var client = CreateClient(apiUrl, token);
            var repos = await client.Repository.GetAllForCurrent(
                new RepositoryRequest { Sort = RepositorySort.Pushed, Direction = SortDirection.Descending },
                new ApiOptions { PageSize = 100, PageCount = 1 });

            return repos.Select(r => (r.FullName, r.Owner.Login, r.Name)).ToList();
        }
        catch
        {
            return Array.Empty<(string, string, string)>();
        }
    }

    private static GitHubClient CreateClient(string apiUrl, string token)
    {
        var client = new GitHubClient(
            new ProductHeaderValue("KiroWebUI-Pipeline"),
            new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
        return client;
    }
}
