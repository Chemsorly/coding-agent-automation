using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Downloads images referenced in issues/PRs with SSRF protection, budget enforcement,
/// content validation, and manual redirect handling.
/// </summary>
public sealed class ImageDownloadService : IDisposable
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    private static readonly Dictionary<string, string> ContentTypeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif"
    };

    private static readonly Dictionary<string, byte[]> MagicBytesMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = [0x89, 0x50, 0x4E, 0x47],
        ["image/jpeg"] = [0xFF, 0xD8, 0xFF],
        ["image/gif"] = [0x47, 0x49, 0x46, 0x38],
        ["image/webp"] = [0x52, 0x49, 0x46, 0x46] // "RIFF" prefix
    };

    private const int MaxRedirects = 3;
    private const int MaxConcurrency = 3;
    private const int ThroughputWindowSeconds = 5;
    private const int MinBytesPerSecond = 1024;
    private const int MaxDimension = 8192;
    private const int MinDimension = 32;

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public ImageDownloadService(
        HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        ILogger? logger = null)
    {
        _logger = logger;

        if (handler is not null)
        {
            _httpClient = new HttpClient(handler, disposeHandler: false);
        }
        else
        {
            var ssrfHandler = SsrfGuard.CreateHandler(dnsResolver);
            _httpClient = new HttpClient(ssrfHandler, disposeHandler: true);
        }
    }

    public async Task<IReadOnlyList<DownloadedImage>> DownloadAllAsync(
        IReadOnlyList<ImageReference> images,
        string targetDirectory,
        string authToken,
        string? gitlabApiUrl,
        string? gitlabProjectId,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        if (!config.EnableIssueImageExtraction)
            return [];

        if (images.Count == 0)
            return [];

        // Limit input to MaxIssueImages
        var imagesToProcess = images.Count > config.MaxIssueImages
            ? images.Take(config.MaxIssueImages).ToList()
            : images;

        var results = new List<DownloadedImage>();
        var budget = new ByteBudget();

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.TotalImageDownloadTimeoutSeconds));

        var tasks = imagesToProcess.Select(async imageRef =>
        {
            await semaphore.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                // Check total budget before starting
                if (budget.TotalBytes >= config.MaxTotalImageSizeBytes)
                    return null;

                var result = await DownloadSingleAsync(
                    new ImageDownloadContext(imageRef, targetDirectory, authToken, gitlabApiUrl, gitlabProjectId,
                        config, budget),
                    timeoutCts.Token).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to download image {Url}", imageRef.Url);
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var downloadResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        results.AddRange(downloadResults.Where(result => result is not null)!);

        return results;
    }

    private async Task<DownloadedImage?> DownloadSingleAsync(
        ImageDownloadContext ctx,
        CancellationToken ct)
    {
        var imageRef = ctx.ImageRef;
        var config = ctx.Config;
        var budget = ctx.Budget;
        var url = ResolveUrl(imageRef.Url, ctx.GitlabApiUrl, ctx.GitlabProjectId);
        if (url is null)
            return null;

        // Scheme validation: https only
        if (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("Skipping non-HTTPS URL: {Url}", imageRef.Url);
            return null;
        }

        // Manual redirect following with auth stripping on cross-origin
        var (response, finalUrl) = await FollowRedirectsAsync(imageRef, url, ctx.AuthToken, ct);
        if (response is null)
            return null;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return null;

            if (finalUrl is null)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var finalExtension = ValidateAndResolveExtension(contentType, finalUrl, imageRef);
            if (finalExtension is null)
                return null;

            var filename = $"{Guid.NewGuid():N}{finalExtension}";
            var filePath = Path.Combine(ctx.TargetDirectory, filename);

            // Per-image size pre-check via Content-Length header (before downloading)
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > config.MaxImageSizeBytes)
            {
                _logger?.LogDebug("Per-image size exceeded via Content-Length for {Url}: {Bytes} bytes", imageRef.Url, contentLength.Value);
                return null;
            }

            var bytesDownloaded = await StreamWithBudgetAsync(response, imageRef, filePath, config, ct);
            if (bytesDownloaded == 0)
            {
                TryDeleteFile(filePath);
                return null;
            }

            return PostDownloadValidate(filePath, filename, imageRef, contentType!, bytesDownloaded, budget, config);
        }
    }

    /// <summary>
    /// Follows HTTP redirects up to MaxRedirects, stripping auth on cross-origin redirects.
    /// Returns the final non-redirect response and final URI, or (null, null) on failure.
    /// </summary>
    private async Task<(HttpResponseMessage? response, Uri? finalUrl)> FollowRedirectsAsync(
        ImageReference imageRef, Uri startUrl, string authToken, CancellationToken ct)
    {
        var currentUrl = startUrl;
        var isGitLabRelative = imageRef.Url.StartsWith('/');
        var stripAuth = false;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            if (redirectCount == MaxRedirects)
            {
                _logger?.LogDebug("Max redirects exceeded for {Url}", imageRef.Url);
                return (null, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            if (!stripAuth)
                ApplyAuthHeaders(request, currentUrl, authToken, isGitLabRelative);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger?.LogDebug(ex, "HTTP request failed for {Url}", currentUrl);
                return (null, null);
            }

            if (!IsRedirect(response.StatusCode))
                return (response, currentUrl);

            var location = response.Headers.Location;
            response.Dispose();

            if (location is null)
                return (null, null);

            var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUrl, location);

            if (IsNonHttpsRedirect(redirectUri))
            {
                _logger?.LogDebug("Redirect to non-HTTPS blocked: {Url}", redirectUri);
                return (null, null);
            }

            if (IsCrossOriginRedirect(currentUrl, redirectUri))
                stripAuth = true;

            currentUrl = redirectUri;
        }

        return (null, null);
    }

    /// <summary>Returns true when the redirect target scheme is not HTTPS, which should be blocked.</summary>
    private static bool IsNonHttpsRedirect(Uri redirectUri) =>
        !string.Equals(redirectUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true when the redirect crosses origins (different host/port), requiring auth stripping.</summary>
    private static bool IsCrossOriginRedirect(Uri from, Uri to) => !IsSameOrigin(from, to);

    /// <summary>
    /// Validates Content-Type and extension consistency, returns the final extension to use,
    /// or null if validation fails.
    /// </summary>
    private string? ValidateAndResolveExtension(string? contentType, Uri finalUrl, ImageReference imageRef)
    {
        if (contentType is null || !AllowedContentTypes.Contains(contentType))
        {
            _logger?.LogDebug("Invalid content type {ContentType} for {Url}", contentType, imageRef.Url);
            return null;
        }

        var urlExtension = GetUrlExtension(finalUrl);
        var expectedExtension = ContentTypeToExtension.GetValueOrDefault(contentType);

        if (urlExtension is not null && expectedExtension is not null && !ExtensionMatchesContentType(urlExtension, contentType))
        {
            _logger?.LogDebug("Extension/Content-Type mismatch: ext={Ext}, ct={CT}", urlExtension, contentType);
            return null;
        }

        var finalExtension = urlExtension ?? expectedExtension ?? ".png";
        if (!AllowedExtensions.Contains(finalExtension))
        {
            _logger?.LogDebug("Disallowed extension {Ext}", finalExtension);
            return null;
        }

        return finalExtension;
    }

    /// <summary>
    /// Streams response content to a file with per-image size cap and throughput detection.
    /// Returns bytes downloaded, or 0 if aborted (file is cleaned up by caller).
    /// When bytesDownloaded exceeds MaxImageSizeBytes, the loop breaks with a non-zero value;
    /// the caller must pass the result to PostDownloadValidate which performs the over-size check
    /// and cleans up the file in its finally block.
    /// TODO: [WARNING] The file-cleanup contract is split: zero-bytes cleanup is documented here
    /// ("cleaned up by caller"), but over-size cleanup is silently delegated to PostDownloadValidate.
    /// Future callers that omit PostDownloadValidate will leave partial files on disk.
    /// </summary>
    private async Task<long> StreamWithBudgetAsync(
        HttpResponseMessage response,
        ImageReference imageRef,
        string filePath,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        long bytesDownloaded = 0;
        try
        {
            await using var contentStream = await GetDecompressedStream(response).ConfigureAwait(false);
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            var buffer = new byte[8192];
            var windowStart = Environment.TickCount64;
            long windowBytes = 0;

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                bytesDownloaded += read;
                windowBytes += read;

                if (bytesDownloaded > config.MaxImageSizeBytes)
                {
                    _logger?.LogDebug("Per-image size exceeded during streaming for {Url}: {Bytes} bytes", imageRef.Url, bytesDownloaded);
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                var elapsed = Environment.TickCount64 - windowStart;
                if (elapsed >= ThroughputWindowSeconds * 1000)
                {
                    var bytesPerSecond = windowBytes * 1000.0 / elapsed;
                    if (bytesPerSecond < MinBytesPerSecond)
                    {
                        _logger?.LogDebug("Throughput below floor for {Url}: {Bps} B/s", imageRef.Url, bytesPerSecond);
                        break;
                    }
                    windowStart = Environment.TickCount64;
                    windowBytes = 0;
                }
            }
        }
        catch (Exception) when (File.Exists(filePath))
        {
            TryDeleteFile(filePath);
            throw;
        }

        return bytesDownloaded;
    }

    /// <summary>
    /// Validates magic bytes, dimensions, and budget after download.
    /// Returns the completed DownloadedImage on success, null if any check fails (cleans up file).
    /// </summary>
    private DownloadedImage? PostDownloadValidate(
        string filePath,
        string filename,
        ImageReference imageRef,
        string contentType,
        long bytesDownloaded,
        ByteBudget budget,
        PipelineConfiguration config)
    {
        var downloadSucceeded = false;
        try
        {
            if (bytesDownloaded > config.MaxImageSizeBytes)
                return null;

            if (!ValidateMagicBytes(filePath, contentType))
            {
                _logger?.LogDebug("Magic bytes mismatch for {Url}", imageRef.Url);
                return null;
            }

            if (!budget.TryReserve(bytesDownloaded, config.MaxTotalImageSizeBytes))
            {
                _logger?.LogDebug("Total budget would be exceeded for {Url}", imageRef.Url);
                return null;
            }

            if (!ValidateDimensions(filePath))
            {
                _logger?.LogDebug("Dimension validation failed for {Url}", imageRef.Url);
                return null;
            }

            downloadSucceeded = true;
            return new DownloadedImage
            {
                LocalPath = filePath,
                LocalFilename = filename,
                Reference = imageRef,
                FileSizeBytes = bytesDownloaded,
                MimeType = contentType
            };
        }
        finally
        {
            if (!downloadSucceeded)
                TryDeleteFile(filePath);
        }
    }

    private static Uri? ResolveUrl(string rawUrl, string? gitlabApiUrl, string? gitlabProjectId)
    {
        // GitLab relative URL: /uploads/{secret}/{filename}
        if (rawUrl.StartsWith('/') && gitlabApiUrl is not null && gitlabProjectId is not null)
        {
            // Construct: {gitlabApiUrl}/projects/{id}{rawUrl}
            var baseUrl = gitlabApiUrl.TrimEnd('/');
            var fullUrl = $"{baseUrl}/projects/{gitlabProjectId}{rawUrl}";
            return Uri.TryCreate(fullUrl, UriKind.Absolute, out var uri) ? uri : null;
        }

        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var absoluteUri) ? absoluteUri : null;
    }

    private static void ApplyAuthHeaders(HttpRequestMessage request, Uri url, string authToken, bool isGitLabRelative)
    {
        if (isGitLabRelative)
        {
            // GitLab: PRIVATE-TOKEN header
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", authToken);
            return;
        }

        if (IsGitHubHost(url))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        }
        // Non-GitHub, non-GitLab hosts: no auth
    }

    private static bool IsGitHubHost(Uri url)
    {
        var host = url.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrigin(Uri a, Uri b)
    {
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
               a.Port == b.Port &&
               string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved or HttpStatusCode.Found or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
            (HttpStatusCode)308;
    }

    private static string? GetUrlExtension(Uri url)
    {
        var path = url.AbsolutePath;
        var lastSegment = path.Split('/').LastOrDefault() ?? string.Empty;

        var dotIndex = lastSegment.LastIndexOf('.');
        if (dotIndex < 0)
            return null;

        var ext = lastSegment[dotIndex..];
        // Remove anything after the extension that's not alphanumeric
        var cleanExt = new string(ext.TakeWhile(c => c == '.' || char.IsLetterOrDigit(c)).ToArray());

        return AllowedExtensions.Contains(cleanExt) ? cleanExt : null;
    }

    private static bool ExtensionMatchesContentType(string extension, string contentType)
    {
        var expectedExt = ContentTypeToExtension.GetValueOrDefault(contentType);
        if (expectedExt is null)
            return false;

        // .jpg and .jpeg both map to image/jpeg
        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        return extension.Equals(expectedExt, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Stream> GetDecompressedStream(HttpResponseMessage response)
    {
        var contentEncoding = response.Content.Headers.ContentEncoding;
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        if (contentEncoding.Contains("gzip"))
            return new GZipStream(stream, CompressionMode.Decompress);
        if (contentEncoding.Contains("deflate"))
            return new DeflateStream(stream, CompressionMode.Decompress);

        return stream;
    }

    /// <summary>
    /// Validates that file magic bytes match the claimed Content-Type.
    /// </summary>
    public static bool ValidateMagicBytes(string filePath, string contentType)
    {
        if (!MagicBytesMap.TryGetValue(contentType, out var expected))
            return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < expected.Length)
                return false;

            // For WebP, also check bytes 8-11 for "WEBP"
            if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
            {
                if (fs.Length < 12)
                    return false;
                var header = new byte[12];
                var read = fs.Read(header, 0, 12);
                if (read < 12)
                    return false;
                // Check "RIFF" prefix
                if (!header.AsSpan(0, 4).SequenceEqual(expected))
                    return false;
                // Check "WEBP" at offset 8
                return header[8] == (byte)'W' && header[9] == (byte)'E' &&
                       header[10] == (byte)'B' && header[11] == (byte)'P';
            }

            var buffer = new byte[expected.Length];
            var bytesRead = fs.Read(buffer, 0, expected.Length);
            if (bytesRead < expected.Length)
                return false;

            return buffer.AsSpan(0, expected.Length).SequenceEqual(expected);
        }
        catch
        {
            return false;
        }
    }

    private bool ValidateDimensions(string filePath)
    {
        try
        {
            using var image = NetVips.Image.NewFromFile(filePath, access: NetVips.Enums.Access.Sequential);
            var width = image.Width;
            var height = image.Height;

            if (width > MaxDimension || height > MaxDimension)
            {
                _logger?.LogDebug("Image too large: {W}x{H}", width, height);
                return false;
            }

            if (width < MinDimension || height < MinDimension)
            {
                _logger?.LogDebug("Image too small: {W}x{H}", width, height);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Graceful degradation: if NetVips can't read the image, allow it through
            _logger?.LogDebug(ex, "NetVips dimension validation failed, allowing image through");
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
