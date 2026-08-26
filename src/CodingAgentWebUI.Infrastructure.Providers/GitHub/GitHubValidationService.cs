using Octokit;
using Serilog;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Lightweight helper for validating GitHub tokens and listing accessible repositories.
/// Used by the Settings page for provider configuration and validation.
/// </summary>
public class GitHubValidationService
{
    private readonly ILogger _logger = Log.Logger;
    private readonly IProviderFactory? _providerFactory;

    /// <summary>Groups GitHub App credentials used across validation methods.</summary>
    private sealed record AppCredentials(string ApiUrl, string ClientId, long InstallationId, string PrivateKeyBase64);

    public GitHubValidationService() { }

    public GitHubValidationService(IProviderFactory providerFactory)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        _providerFactory = providerFactory;
    }

    /// <summary>
    /// Validates GitHub App credentials by generating a JWT, exchanging it for an installation token,
    /// and verifying access by listing installation repositories or checking specific repository access.
    /// Returns user-friendly error messages for all failure modes.
    /// When owner/repo are provided and a provider factory is available, delegates the credential +
    /// repo access check to the provider's <see cref="IIssueProvider.ValidateAsync"/>.
    /// </summary>
    public async Task<(bool Success, string Message)> ValidateAppCredentialsAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, CancellationToken ct,
        string? owner = null, string? repo = null)
    {
        var credentials = new AppCredentials(apiUrl, clientId, installationId, privateKeyBase64);

        // Step 1: Create a temporary auth service and get a token
        var (tokenSuccess, token, tokenError) = await AuthenticateAndGetTokenAsync(credentials, ct);
        if (!tokenSuccess) return (false, tokenError!);

        // Step 2: If no owner/repo, just verify the token works by listing repos
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return await ValidateInstallationWithoutRepoAsync(token!, apiUrl);

        // Step 3+4: owner/repo provided — verify repository access and permissions
        return await ValidateRepositoryPermissionsAsync(token!, credentials, owner, repo, ct);
    }

    private async Task<(bool Success, string? Token, string? Error)> AuthenticateAndGetTokenAsync(
        AppCredentials credentials, CancellationToken ct)
    {
        try
        {
            var authService = new GitHubAppAuthService(
                credentials.ClientId, credentials.InstallationId, credentials.PrivateKeyBase64, credentials.ApiUrl, _logger);
            var token = await authService.GetTokenAsync(ct);
            return (true, token, null);
        }
        catch (GitHubAuthException ex) when (ex.ErrorKind == GitHubAuthErrorKind.PrivateKeyDecodeFailure)
        {
            return (false, null, "Invalid private key: could not decode from base64");
        }
        catch (GitHubAuthException ex) when (ex.ErrorKind == GitHubAuthErrorKind.TokenExchangeFailure)
        {
            return (false, null, $"Authentication failed: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Message)> ValidateInstallationWithoutRepoAsync(
        string token, string apiUrl)
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

    private async Task<(bool Success, string Message)> ValidateRepositoryPermissionsAsync(
        string token, AppCredentials credentials,
        string owner, string repo, CancellationToken ct)
    {
        // NOTE: [GH-06] Provider delegation (below) already validates credentials + repo access via provider.ValidateAsync.
        // The subsequent Repository.Get call is redundant for access validation — only the permission extraction
        // (read/write/admin) is needed. Refactor to skip the redundant API call.
        if (_providerFactory is not null)
        {
            try
            {
                var config = new Pipeline.Models.ProviderConfig
                {
                    Id = "validation-temp",
                    Kind = Pipeline.Models.ProviderKind.Issue,
                    ProviderType = "GitHub",
                    DisplayName = "Validation",
                    Settings = new Dictionary<string, string>
                    {
                        [ProviderSettingKeys.ApiUrl] = credentials.ApiUrl,
                        [ProviderSettingKeys.ClientId] = credentials.ClientId,
                        [ProviderSettingKeys.InstallationId] = credentials.InstallationId.ToString(),
                        [ProviderSettingKeys.PrivateKeyBase64] = credentials.PrivateKeyBase64,
                        [ProviderSettingKeys.Owner] = owner,
                        [ProviderSettingKeys.Repo] = repo
                    }
                };
                await using var provider = _providerFactory.CreateIssueProvider(config);
                await provider.ValidateAsync(ct);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        try
        {
            var client = CreateClient(credentials.ApiUrl, token);
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
