using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Clones a project's other repositories into folders of the workspace, for an agent that only reads them: a project
/// epic's decomposition (<see cref="CloneProjectRepositoriesStep"/>) or a project review
/// (<see cref="CloneProjectReviewRepositoriesStep"/>).
/// </summary>
/// <remarks>
/// Each clone is sealed (see <see cref="SealAsync"/>): a repository that cannot get a read-only token keeps its own
/// token, so nothing in the workspace may keep that token, and the clone must not be able to push.
/// Clone failures are non-critical: the repository's <see cref="RepositoryTarget.LocalPath"/> stays null and a warning
/// is logged. Clones run in parallel (up to 3 at a time), each with a timeout of 120 seconds.
/// </remarks>
internal static class ProjectRepositoryCloner
{
    /// <summary>The push URL of a sealed clone: not a repository, so a push fails.</summary>
    internal const string DisabledPushUrl = "PUSH_DISABLED_read-only-project-clone";

    /// <summary>Points git at the clone's own git directory, so it never falls back to a repository further up.</summary>
    private const string OwnGitDir = "--git-dir=.git";

    private const int MaxParallelClones = 3;
    private const int MaxFolderNameLength = 100;
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Clones each of <paramref name="providers"/> into <paramref name="relativeDirectory"/>/<c>{folder}</c> and sets the
    /// <see cref="RepositoryTarget.LocalPath"/> of the target with the same template name.
    /// </summary>
    internal static async Task CloneAsync(
        IReadOnlyList<(string TemplateName, IRepositoryProvider Provider)> providers,
        IReadOnlyList<RepositoryTarget> targets,
        string relativeDirectory,
        PipelineStepContext context,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(context.Run.WorkspacePath!, relativeDirectory));

        context.Callbacks.EmitOutputLine($"📦 Cloning {providers.Count} additional project repo(s)...");

        // Folder names are assigned up front, because two template names can map to the same folder name
        // (for example "web app" and "web_app") and the clones run in parallel.
        using var semaphore = new SemaphoreSlim(MaxParallelClones);
        var usedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = providers
            .Select(p => (p.TemplateName, p.Provider, Folder: UniqueFolderName(ToFolderName(p.TemplateName), usedFolderNames)))
            .Select(p => CloneRepoAsync(p.TemplateName, $"{relativeDirectory}/{p.Folder}", p.Provider, targets, context, semaphore, ct))
            .ToList();

        await Task.WhenAll(tasks);

        var clonedCount = targets.Count(r => r.LocalPath is not null);
        var failedCount = providers.Count - clonedCount;
        if (failedCount > 0)
            context.Callbacks.EmitOutputLine($"⚠️ {failedCount} repo(s) failed to clone — marked unavailable");
        else
            context.Callbacks.EmitOutputLine($"✅ All {clonedCount} additional repo(s) cloned successfully");
    }

    private static async Task CloneRepoAsync(
        string templateName,
        string relativePath,
        IRepositoryProvider provider,
        IReadOnlyList<RepositoryTarget> targets,
        PipelineStepContext context,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var targetDir = Path.Combine(context.Run.WorkspacePath!, relativePath);

            using var timeoutCts = new CancellationTokenSource(CloneTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                Directory.CreateDirectory(targetDir);
                // A GitLab repository learns its clone URL from the project metadata that validation loads
                await provider.ValidateAsync(linkedCts.Token);
                await provider.CloneAsync(targetDir, linkedCts.Token);
                await SealOrRemoveAsync(targetDir, linkedCts.Token);

                var target = targets.FirstOrDefault(
                    r => string.Equals(r.TemplateName, templateName, StringComparison.Ordinal));
                if (target is not null)
                    target.LocalPath = relativePath;

                context.Logger.Information("Cloned additional repo '{TemplateName}' to '{TargetPath}'",
                    templateName, relativePath);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                context.Logger.Warning("Clone of repo '{TemplateName}' timed out after {Timeout}s — marking unavailable",
                    templateName, CloneTimeout.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate pipeline-level cancellation
            }
            catch (Exception ex)
            {
                Activity.Current?.RecordError(ex, ct);
                context.Logger.Warning(ex, "Failed to clone additional repo '{TemplateName}' — marking unavailable: {Error}",
                    templateName, ex.Message);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Seals the clone in <paramref name="cloneDir"/>; a clone that cannot be sealed is deleted.</summary>
    private static async Task SealOrRemoveAsync(string cloneDir, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(cloneDir, ".git")))
            return; // nothing was checked out as a repository, so there is no remote to seal

        try
        {
            await SealAsync(cloneDir, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Directory.Delete(cloneDir, recursive: true);
            throw new InvalidOperationException("The clone could not be made read-only, so it was removed.", ex);
        }
    }

    /// <summary>
    /// Makes a clone read-only for the agent: the credentials leave the <c>origin</c> URL, pushing is turned off, and
    /// <c>FETCH_HEAD</c> and the reflogs go, as git records the clone URL there, credentials included.
    /// </summary>
    /// <remarks>
    /// The providers clone with LibGit2Sharp, which takes the credentials from a callback, so a token reaches the
    /// disk only inside a clone URL that carries it (GitLab's), in the places above.
    /// Every git command names the clone's own git directory. The clone lies inside the workspace's repository, so
    /// with a broken clone git would otherwise find that repository instead and change its remote.
    /// </remarks>
    internal static async Task SealAsync(string cloneDir, CancellationToken ct)
    {
        var originUrl = (await GitProcessRunner.RunAsync(cloneDir, $"{OwnGitDir} remote get-url origin", ct)).Trim();
        var cleanUrl = WithoutCredentials(originUrl);
        // A changed URL is an escaped AbsoluteUri, so it has no spaces or quotes that would split the arguments
        if (!string.Equals(cleanUrl, originUrl, StringComparison.Ordinal))
            await GitProcessRunner.RunAsync(cloneDir, $"{OwnGitDir} remote set-url origin {cleanUrl}", ct);

        await GitProcessRunner.RunAsync(cloneDir, $"{OwnGitDir} remote set-url --push origin {DisabledPushUrl}", ct);

        var gitDir = Path.Combine(cloneDir, ".git");
        File.Delete(Path.Combine(gitDir, "FETCH_HEAD"));
        var logsDir = Path.Combine(gitDir, "logs");
        if (Directory.Exists(logsDir))
            Directory.Delete(logsDir, recursive: true);
    }

    /// <summary><paramref name="url"/> without the user name and password in it, if it has any.</summary>
    internal static string WithoutCredentials(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return url;

        return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
    }

    /// <summary>
    /// The folder a project repository is cloned into, from its template name: every character other
    /// than an ASCII letter, a digit, '-', '_' or '.' becomes '_', and leading or trailing dots are
    /// removed. The result is always a single folder (no separators, no "..").
    /// </summary>
    internal static string ToFolderName(string templateName)
    {
        var safe = new string(templateName
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
                .Take(MaxFolderNameLength)
                .ToArray())
            .Trim('.');

        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>
    /// <paramref name="folderName"/>, or the first of <c>folderName_2</c>, <c>folderName_3</c>, … that is
    /// not in <paramref name="usedFolderNames"/>; the result is added to the set.
    /// </summary>
    internal static string UniqueFolderName(string folderName, HashSet<string> usedFolderNames)
    {
        var candidate = folderName;
        for (var suffix = 2; !usedFolderNames.Add(candidate); suffix++)
            candidate = $"{folderName}_{suffix}";
        return candidate;
    }
}
