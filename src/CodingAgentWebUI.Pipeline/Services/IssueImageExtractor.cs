using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Extracts image URLs from issue/PR markdown bodies and comments.
/// Handles inline images, reference-style images, HTML img tags, and clickable thumbnails.
/// Filters out badges, avatars, videos, and SVGs. Deduplicates by URL.
/// </summary>
public sealed partial class IssueImageExtractor
{
    private static readonly HashSet<string> BadgeDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "shields.io",
        "img.shields.io",
        "badge.fury.io",
        "badgen.net",
        "codecov.io",
        "coveralls.io",
        "api.travis-ci.org",
        "api.travis-ci.com",
        "circleci.com",
        "app.codacy.com",
        "sonarcloud.io"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".webm", ".avi"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif"
    };

    // Pass 1: Reference-style link definitions: [ref]: url
    [GeneratedRegex(@"^\s*\[([^\]]+)\]:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex ReferenceLinkDefinitionPattern();

    // Inline image: ![alt](url) or ![alt](url "title") — with balanced single-depth parens
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^()\s]+(?:\([^()]*\)[^()\s]*)*)(?:\s+""[^""]*"")?\)")]
    private static partial Regex InlineImagePattern();

    // Reference image: ![alt][ref]
    [GeneratedRegex(@"!\[([^\]]*)\]\[([^\]]+)\]")]
    private static partial Regex ReferenceImagePattern();

    // HTML img tag: <img ... src="url" ...> (case-insensitive, any attribute order)
    [GeneratedRegex(@"<img\s[^>]*?src\s*=\s*[""']([^""']+)[""'][^>]*?>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlImgPattern();

    // Clickable thumbnail: [![alt](thumb-url)](link-url) — we extract inner image only
    [GeneratedRegex(@"\[!\[([^\]]*)\]\(([^()\s]+(?:\([^()]*\)[^()\s]*)*)(?:\s+""[^""]*"")?\)\]\([^)]+\)")]
    private static partial Regex ClickableThumbnailPattern();

    // Fenced code block delimiter: ``` or ~~~
    [GeneratedRegex(@"^(\s*)(```|~~~)")]
    private static partial Regex FenceDelimiterPattern();

    /// <summary>
    /// Extracts image references from an issue/PR body and optional comments.
    /// </summary>
    public IReadOnlyList<ImageReference> Extract(
        string body,
        IReadOnlyList<IssueComment>? comments,
        string sourceIdentifier,
        ImageSourceKind sourceKind)
    {
        var urlToReference = new Dictionary<string, ImageReference>(StringComparer.Ordinal);
        var orderedUrls = new List<string>();

        // Extract from body
        if (!string.IsNullOrEmpty(body))
        {
            ExtractFromMarkdown(body, ImageSourceType.Body, 0, urlToReference, orderedUrls);
        }

        // Extract from comments
        if (comments is { Count: > 0 })
        {
            for (var i = 0; i < comments.Count; i++)
            {
                var commentBody = comments[i].Body;
                if (!string.IsNullOrEmpty(commentBody))
                {
                    ExtractFromMarkdown(commentBody, ImageSourceType.Comment, i, urlToReference, orderedUrls);
                }
            }
        }

        // Return ordered, deduplicated references
        return orderedUrls.Select(url => urlToReference[url]).ToList();
    }

    /// <summary>
    /// Generates the local filename for an image reference at a given index.
    /// </summary>
    public static string GetFilename(string sourceIdentifier, ImageSourceKind sourceKind, int index, string url)
    {
        var kindPrefix = sourceKind == ImageSourceKind.Issue ? "issue" : "pr";
        var extension = DetermineExtension(url);
        return $"{kindPrefix}-{sourceIdentifier}-image-{(index + 1):D3}{extension}";
    }

    private void ExtractFromMarkdown(
        string markdown,
        ImageSourceType sourceType,
        int sourceIndex,
        Dictionary<string, ImageReference> urlToReference,
        List<string> orderedUrls)
    {
        var lines = markdown.Split('\n');
        var nonCodeLines = ExcludeCodeBlocks(lines);
        var nonCodeText = string.Join('\n', nonCodeLines);

        // Pass 1: Build reference definition dictionary
        var referenceDefinitions = BuildReferenceDefinitions(nonCodeLines);

        // Pass 2: Extract images
        var extractedUrls = new List<(string Url, string AltText)>();

        // Clickable thumbnails first (they contain inline images — extract inner only)
        foreach (Match match in ClickableThumbnailPattern().Matches(nonCodeText))
        {
            var alt = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            extractedUrls.Add((url, alt));
        }

        // Remove clickable thumbnails from text to avoid double-extracting the inner image
        var textWithoutThumbnails = ClickableThumbnailPattern().Replace(nonCodeText, "");

        // Inline images
        foreach (Match match in InlineImagePattern().Matches(textWithoutThumbnails))
        {
            var alt = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            extractedUrls.Add((url, alt));
        }

        // Reference images
        foreach (Match match in ReferenceImagePattern().Matches(textWithoutThumbnails))
        {
            var alt = match.Groups[1].Value;
            var refKey = match.Groups[2].Value;
            if (referenceDefinitions.TryGetValue(refKey, out var refUrl))
            {
                extractedUrls.Add((refUrl, alt));
            }
        }

        // HTML img tags
        foreach (Match match in HtmlImgPattern().Matches(textWithoutThumbnails))
        {
            var url = match.Groups[1].Value;
            extractedUrls.Add((url, ""));
        }

        // Filter and deduplicate
        foreach (var (url, alt) in extractedUrls)
        {
            if (ShouldFilter(url))
                continue;

            if (!urlToReference.ContainsKey(url))
            {
                urlToReference[url] = new ImageReference
                {
                    Url = url,
                    AltText = alt,
                    SourceType = sourceType,
                    SourceIndex = sourceIndex
                };
                orderedUrls.Add(url);
            }
        }
    }

    /// <summary>
    /// Returns lines that are NOT inside code blocks (fenced or indented).
    /// </summary>
    private static List<string> ExcludeCodeBlocks(string[] lines)
    {
        var result = new List<string>(lines.Length);
        var insideFence = false;
        var previousLineBlank = false;

        foreach (var line in lines)
        {
            // Check for fence delimiter
            if (FenceDelimiterPattern().IsMatch(line))
            {
                insideFence = !insideFence;
                previousLineBlank = false;
                continue; // Skip the fence line itself
            }

            if (insideFence)
            {
                previousLineBlank = false;
                continue; // Skip lines inside fenced code blocks
            }

            // Check for indented code block (4+ spaces after a blank line)
            if (previousLineBlank && line.Length >= 4 && line.StartsWith("    "))
            {
                // Skip indented code block line
                continue;
            }

            previousLineBlank = string.IsNullOrWhiteSpace(line);
            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Pass 1: Builds a dictionary of reference-style link definitions.
    /// </summary>
    private static Dictionary<string, string> BuildReferenceDefinitions(List<string> lines)
    {
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fullText = string.Join('\n', lines);

        foreach (Match match in ReferenceLinkDefinitionPattern().Matches(fullText))
        {
            var key = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            definitions.TryAdd(key, url);
        }

        return definitions;
    }

    /// <summary>
    /// Returns true if the URL should be filtered out (badges, videos, SVGs, avatars).
    /// </summary>
    private static bool ShouldFilter(string url)
    {
        // Try to parse as URI to check host
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;

            // Badge domains
            if (BadgeDomains.Contains(host))
                return true;

            // Avatar URLs
            if (host.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return true;

            // GitHub Actions workflow badge
            if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Contains("/workflows/", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.EndsWith("/badge.svg", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Path-based heuristics (works for both absolute and relative URLs)
        var path = GetPathFromUrl(url);

        // URL path containing /badge or ending in badge.svg
        if (path.Contains("/badge", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.EndsWith("badge.svg", StringComparison.OrdinalIgnoreCase))
            return true;

        // SVG files
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return true;

        // Video extensions
        var ext = GetExtensionFromPath(path);
        if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext))
            return true;

        return false;
    }

    /// <summary>
    /// Determines the file extension for a filename assignment.
    /// </summary>
    internal static string DetermineExtension(string url)
    {
        var path = GetPathFromUrl(url);
        var ext = GetExtensionFromPath(path);

        if (!string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext))
            return ext.ToLowerInvariant();

        // Default to .png for extensionless URLs (GitHub asset UUIDs, etc.)
        return ".png";
    }

    /// <summary>
    /// Extracts the path component from a URL (handles both absolute and relative).
    /// </summary>
    private static string GetPathFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath;

        // Relative URL — strip query string
        var queryIndex = url.IndexOf('?');
        return queryIndex >= 0 ? url[..queryIndex] : url;
    }

    /// <summary>
    /// Extracts file extension from a path, handling percent-encoded filenames.
    /// </summary>
    private static string GetExtensionFromPath(string path)
    {
        // Strip query/fragment if present
        var cleanPath = path;
        var queryIdx = cleanPath.IndexOf('?');
        if (queryIdx >= 0) cleanPath = cleanPath[..queryIdx];
        var fragmentIdx = cleanPath.IndexOf('#');
        if (fragmentIdx >= 0) cleanPath = cleanPath[..fragmentIdx];

        // Get extension from the last segment
        var lastSlash = cleanPath.LastIndexOf('/');
        var filename = lastSlash >= 0 ? cleanPath[(lastSlash + 1)..] : cleanPath;

        // Handle percent-encoded filenames
        var decodedFilename = Uri.UnescapeDataString(filename);
        var dotIndex = decodedFilename.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex < decodedFilename.Length - 1)
        {
            return "." + decodedFilename[(dotIndex + 1)..].ToLowerInvariant();
        }

        return string.Empty;
    }
}
