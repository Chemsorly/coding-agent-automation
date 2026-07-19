using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Downloads issue/PR images to the agent workspace for delivery to the coding agent.
/// Graceful degradation: never fails the pipeline due to image issues.
/// </summary>
public sealed class DownloadIssueImagesStep : IPipelineStep
{
    public string StepName => "DownloadIssueImages";

    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly ProviderConfig _repoConfig;

    public DownloadIssueImagesStep(
        Func<CancellationToken, Task<string>> tokenProvider,
        ProviderConfig repoConfig)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(repoConfig);
        _tokenProvider = tokenProvider;
        _repoConfig = repoConfig;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (!context.Config.EnableIssueImageExtraction)
        {
            context.Logger.Debug("DownloadIssueImages skipped: EnableIssueImageExtraction is disabled (RunId={RunId})",
                context.Run.RunId);
            return StepResult.Continue;
        }

        if (!context.AgentProvider.SupportsVisionInput)
        {
            context.Logger.Debug("DownloadIssueImages skipped: agent model does not support vision input (RunId={RunId})",
                context.Run.RunId);
            return StepResult.Continue;
        }

        var images = CollectImageReferences(context);
        if (images.Count == 0)
        {
            context.Logger.Debug("DownloadIssueImages skipped: no image references found (RunId={RunId})",
                context.Run.RunId);
            return StepResult.Continue;
        }

        try
        {
            var token = await _tokenProvider(ct);

            var targetDirectory = Path.Combine(context.Run.WorkspacePath!, ".agent", "images");
            Directory.CreateDirectory(targetDirectory);

            var gitlabApiUrl = _repoConfig.Settings.GetValueOrDefault("ApiUrl");
            var gitlabProjectId = _repoConfig.Settings.GetValueOrDefault("ProjectId");

            using var downloadService = new ImageDownloadService();
            var downloaded = await downloadService.DownloadAllAsync(
                images,
                targetDirectory,
                token,
                gitlabApiUrl,
                gitlabProjectId,
                context.Config,
                ct);

            context.DownloadedImages = downloaded;

            context.Logger.Information(
                "DownloadIssueImages completed: {DownloadedCount}/{TotalCount} images downloaded (RunId={RunId})",
                downloaded.Count, images.Count, context.Run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex,
                "DownloadIssueImages failed, continuing without images (RunId={RunId})",
                context.Run.RunId);
        }

        return StepResult.Continue;
    }

    private List<ImageReference> CollectImageReferences(PipelineStepContext context)
    {
        var images = new List<ImageReference>();
        var extractor = new IssueImageExtractor();

        // Issue images (pre-extracted by orchestrator's FetchIssueStep)
        if (context.Issue?.Images is { Count: > 0 } issueImages)
            images.AddRange(issueImages);

        // PR body images (review runs — extract on agent side from raw string)
        if (context.Run.RunType == PipelineRunType.Review
            && !string.IsNullOrWhiteSpace(context.Run.ReviewPrDescription))
        {
            var prNumber = context.Run.IssueIdentifier.Value; // PR number for review runs
            var prImages = extractor.Extract(
                context.Run.ReviewPrDescription, comments: null,
                prNumber, ImageSourceKind.PullRequest);
            images.AddRange(prImages);
        }

        return images;
    }
}
