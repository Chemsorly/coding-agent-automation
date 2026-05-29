using NGitLab;
using NGitLab.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// Result of a GitLab credential and project validation attempt.
/// </summary>
public record GitLabValidationResult(
    bool Success,
    string? ProjectPath,
    string? AccessLevel,
    string? ErrorMessage);

/// <summary>
/// Lightweight service for validating GitLab access tokens and project accessibility.
/// Used by the Settings page GitLab provider form for pre-save validation.
/// </summary>
public sealed class GitLabValidationService
{
    private readonly ILogger _logger = Log.Logger;

    /// <summary>
    /// Validates that the given access token can authenticate with the GitLab API
    /// and access the specified project. Returns the project path and user access level
    /// on success, or a structured failure message on error.
    /// </summary>
    /// <param name="apiUrl">GitLab API base URL (e.g., "https://gitlab.com").</param>
    /// <param name="accessToken">GitLab personal, project, or group access token.</param>
    /// <param name="projectId">Numeric project identifier as a string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="GitLabValidationResult"/> indicating success or failure.</returns>
    public async Task<GitLabValidationResult> ValidateAsync(
        string apiUrl, string accessToken, string projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            return new GitLabValidationResult(false, null, null, "API URL is required.");

        // Validate URL scheme — only HTTP/HTTPS allowed
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != "https" && parsedUri.Scheme != "http"))
        {
            var truncatedUrl = apiUrl.Length > 200 ? apiUrl[..200] + "…" : apiUrl;
            return new GitLabValidationResult(false, null, null,
                $"API URL must use https:// or http:// scheme. Got: '{truncatedUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
            return new GitLabValidationResult(false, null, null, "Access token is required.");

        if (string.IsNullOrWhiteSpace(projectId))
            return new GitLabValidationResult(false, null, null, "Project ID is required.");

        if (!int.TryParse(projectId, out var numericProjectId))
            return new GitLabValidationResult(false, null, null,
                $"Invalid project ID: '{projectId}'. Expected a numeric value.");

        IGitLabClient client;
        try
        {
            var options = new RequestOptions(retryCount: 0, retryInterval: TimeSpan.Zero)
            {
                UserAgent = "CodingAgentAutomation/1.0"
            };
            client = new GitLabClient(apiUrl, accessToken, options);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create GitLab client for validation");
            return new GitLabValidationResult(false, null, null,
                $"Failed to create GitLab client: {ex.Message}");
        }

        try
        {
            // Task.Run wraps the synchronous NGitLab indexer call. Cancellation relies on
            // the HttpClient.Timeout configured in NGitLab rather than the CancellationToken,
            // because the sync indexer does not accept a token parameter.
            var project = await Task.Run(() => client.Projects[numericProjectId], ct);

            var accessLevel = MapAccessLevel(project);

            _logger.Information(
                "GitLab validation succeeded for project {ProjectId} ({ProjectPath}), access: {AccessLevel}",
                numericProjectId, project.PathWithNamespace, accessLevel);

            return new GitLabValidationResult(
                true,
                project.PathWithNamespace,
                accessLevel,
                null);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 401)
        {
            _logger.Warning("GitLab validation failed: invalid credentials for project {ProjectId}", numericProjectId);
            return new GitLabValidationResult(false, null, null,
                "Invalid access token. Verify the token is correct and has not expired.");
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            _logger.Warning("GitLab validation failed: project {ProjectId} not found", numericProjectId);
            return new GitLabValidationResult(false, null, null,
                $"Project {numericProjectId} not found or not accessible. " +
                "Verify the project ID and that the token has access to this project.");
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 403)
        {
            _logger.Warning("GitLab validation failed: insufficient permissions for project {ProjectId}", numericProjectId);
            return new GitLabValidationResult(false, null, null,
                $"Access denied for project {numericProjectId}. " +
                "The token lacks sufficient permissions to access this project.");
        }
        catch (HttpRequestException ex)
        {
            var truncatedUrl = apiUrl.Length > 200 ? apiUrl[..200] + "…" : apiUrl;
            _logger.Warning(ex, "GitLab validation failed: connectivity error for {ApiUrl}", truncatedUrl);
            return new GitLabValidationResult(false, null, null,
                $"Unable to connect to GitLab at '{truncatedUrl}'. Verify the URL is correct and the server is reachable.");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate user-initiated cancellation
        }
        catch (TaskCanceledException ex)
        {
            var truncatedUrl = apiUrl.Length > 200 ? apiUrl[..200] + "…" : apiUrl;
            _logger.Warning(ex, "GitLab validation timed out for {ApiUrl}", truncatedUrl);
            return new GitLabValidationResult(false, null, null,
                $"Request timed out connecting to GitLab at '{truncatedUrl}'.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "GitLab validation failed with unexpected error for project {ProjectId}", numericProjectId);
            return new GitLabValidationResult(false, null, null,
                $"Validation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Maps the project's access level to a human-readable string.
    /// GitLab project metadata includes the current user's access level in the Permissions field.
    /// </summary>
    private static string MapAccessLevel(Project project)
    {
        // NGitLab's Project model exposes Permissions with project_access and group_access.
        // Each contains an access_level integer:
        // 10=Guest, 20=Reporter, 30=Developer, 40=Maintainer, 50=Owner
        var projectAccess = project.Permissions?.ProjectAccess?.AccessLevel;
        var groupAccess = project.Permissions?.GroupAccess?.AccessLevel;

        // Use the higher of project-level or group-level access
        var effectiveLevel = (int?)null;
        if (projectAccess.HasValue) effectiveLevel = (int)projectAccess.Value;
        if (groupAccess.HasValue)
        {
            var groupLevel = (int)groupAccess.Value;
            effectiveLevel = effectiveLevel.HasValue
                ? Math.Max(effectiveLevel.Value, groupLevel)
                : groupLevel;
        }

        return effectiveLevel switch
        {
            >= 50 => "Owner",
            >= 40 => "Maintainer",
            >= 30 => "Developer",
            >= 20 => "Reporter",
            >= 10 => "Guest",
            _ => "Unknown"
        };
    }
}
