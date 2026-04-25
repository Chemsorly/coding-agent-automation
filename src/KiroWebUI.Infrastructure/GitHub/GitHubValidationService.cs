using Octokit;
using Serilog;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Infrastructure.GitHub;

/// <summary>
/// Lightweight helper for validating GitHub tokens and listing accessible repositories.
/// Used by the Settings page for provider configuration and validation.
/// </summary>
public class GitHubValidationService
{
    private readonly ILogger _logger = Log.Logger;

    /// <summary>
    /// Validates GitHub App credentials by generating a JWT, exchanging it for an installation token,
    /// and verifying access by listing installation repositories or checking specific repository access.
    /// Returns user-friendly error messages for all failure modes.
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateAppCredentialsAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, CancellationToken ct,
        string? owner = null, string? repo = null)
    {
        // Step 1: Create a temporary auth service and get a token
        string token;
        try
        {
            var authService = new GitHubAppAuthService(
                clientId, installationId, privateKeyBase64, apiUrl, _logger);
            token = await authService.GetTokenAsync(ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to decode private key"))
        {
            return (false, "Invalid private key: could not decode from base64");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("token exchange failed"))
        {
            return (false, $"Authentication failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }

        // Step 2: Verify the installation token works.
        // If owner/repo are provided, go straight to repo validation (Step 3).
        // Otherwise, list installation repos to confirm the token is accepted.
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            try
            {
                var client = CreateClient(apiUrl, token);
                var response = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent();
                return (true, $"✅ GitHub App credentials validated — {response.TotalCount} repository(ies) accessible");
            }
            catch (AuthorizationException)
            {
                return (false, "Authentication failed: installation token was rejected");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        // Step 3: owner/repo provided — verify repository access and permissions
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
            return (false, $"Repository {owner}/{repo} not found or app lacks access");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists repositories accessible to a GitHub App installation.
    /// Creates a temporary auth service, generates a token, and lists installation repos.
    /// Returns up to 100 repos sorted by most recently pushed.
    /// </summary>
    public async Task<IReadOnlyList<(string FullName, string Owner, string Name)>> ListRepositoriesWithAppAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, CancellationToken ct)
    {
        try
        {
            var authService = new GitHubAppAuthService(
                clientId, installationId, privateKeyBase64, apiUrl, _logger);
            var token = await authService.GetTokenAsync(ct);

            // Installation tokens can access GET /installation/repositories
            // via Octokit's GitHubApps.Installation sub-client
            var client = CreateClient(apiUrl, token);
            var response = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent();

            return (response.Repositories ?? Array.Empty<Octokit.Repository>())
                .Select(r => (r.FullName, r.Owner.Login, r.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to list repositories for GitHub App installation");
            return Array.Empty<(string, string, string)>();
        }
    }

    /// <summary>
    /// Validates that the GitHub App has Actions read access on the specified repository
    /// by attempting to list workflow runs. Returns a user-friendly message.
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateActionsAccessAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64,
        string owner, string repo, CancellationToken ct)
    {
        string token;
        try
        {
            var authService = new GitHubAppAuthService(
                clientId, installationId, privateKeyBase64, apiUrl, _logger);
            token = await authService.GetTokenAsync(ct);
        }
        catch (Exception ex)
        {
            return (false, $"Authentication failed: {ex.Message}");
        }

        try
        {
            var client = CreateClient(apiUrl, token);
            var runs = await client.Actions.Workflows.Runs.List(owner, repo);
            return (true, $"✅ Actions access verified — {runs.TotalCount} workflow run(s) found");
        }
        catch (ForbiddenException)
        {
            return (false, $"GitHub App lacks Actions read permission on {owner}/{repo}");
        }
        catch (NotFoundException)
        {
            return (false, $"Repository {owner}/{repo} not found or app lacks access");
        }
        catch (Exception ex)
        {
            return (false, $"Actions access check failed: {ex.Message}");
        }
    }

    private static GitHubClient CreateClient(string apiUrl, string token)
    {
        var client = new GitHubClient(
            GitHubClientProvider.AppProductHeader,
            new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
        return client;
    }
}
