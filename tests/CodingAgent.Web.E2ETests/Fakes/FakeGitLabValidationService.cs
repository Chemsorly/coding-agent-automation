using CodingAgent.Infrastructure.GitLab;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// No-op GitLab validation service for E2E tests. Always returns a successful validation
/// result so tests can save GitLab providers with dummy credentials without making real
/// HTTP calls to gitlab.com.
///
/// The real <see cref="GitLabValidationService"/> makes network calls that fail in CI
/// (no live GitLab instance, fake credentials). Registering this stub skips validation,
/// matching the intent described in <c>SettingsProviderCrudTests</c>:
/// "GitLab validation is null in the harness, so its null-guard path skips credential checking."
/// </summary>
public sealed class FakeGitLabValidationService : GitLabValidationService
{
    public override Task<GitLabValidationResult> ValidateAsync(
        string apiUrl, string accessToken, string projectId, CancellationToken ct)
        => Task.FromResult(new GitLabValidationResult(
            Success: true,
            ProjectPath: $"e2e-group/project-{projectId}",
            AccessLevel: "Maintainer",
            ErrorMessage: null));
}
