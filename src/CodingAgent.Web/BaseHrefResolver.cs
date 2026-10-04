namespace CodingAgent.Web;

/// <summary>
/// Computes the <c>&lt;base href&gt;</c> value that App.razor renders for each request.
///
/// The browser resolves every relative asset URL (<c>css/app.css</c>, <c>_framework/blazor.web.js</c>)
/// and every relative in-app link against the base href. A fixed relative base such as <c>"./"</c>
/// resolves against the current document URL, so on a nested route like <c>/runs/{id}</c> it points
/// at <c>/runs/</c> and every asset 404s. A fixed absolute base such as <c>"/"</c> fixes that but
/// breaks a reverse proxy that strips its own prefix before forwarding (Rancher's service proxy
/// serves the app under <c>/k8s/clusters/.../proxy/</c>), because <c>"/"</c> then points at the
/// proxy's root.
///
/// For the default (unset or relative) configuration this class therefore emits a relative base that
/// climbs from the current document back to the app root: <c>"./"</c> for <c>/overview</c>,
/// <c>"../"</c> for <c>/runs/{id}</c>. It resolves to the app root both at a root deployment and
/// under a prefix-stripping proxy, with no proxy-specific configuration. A rooted or absolute
/// configured value (e.g. <c>/k8s/clusters/c-xxxxx/proxy/</c>) is used as is.
/// </summary>
internal static class BaseHrefResolver
{
    private const string CurrentDirectory = "./";
    private const string ParentDirectory = "../";
    private const char PathSeparator = '/';

    /// <param name="configuredBasePath">The <c>WebUI:BasePath</c> setting. Null, empty and relative
    /// values (such as the Helm default <c>"./"</c>) are resolved against the app root.</param>
    /// <param name="requestPath">The request path relative to the app's path base
    /// (<c>HttpContext.Request.Path</c>), e.g. <c>/runs/abc</c>.</param>
    /// <returns>The base href, always ending in <c>"/"</c>.</returns>
    public static string Resolve(string? configuredBasePath, string? requestPath)
    {
        var configured = configuredBasePath?.Trim() ?? string.Empty;
        if (IsRootedOrAbsolute(configured))
            return EnsureTrailingSlash(configured);

        return EnsureTrailingSlash(RelativePathToAppRoot(requestPath) + StripCurrentDirectory(configured));
    }

    /// <summary>
    /// The relative path from the directory of the document at <paramref name="requestPath"/> back to
    /// the app root. Every <c>"/"</c> after the leading one is a directory level the browser climbs.
    /// </summary>
    internal static string RelativePathToAppRoot(string? requestPath)
    {
        var depth = string.IsNullOrEmpty(requestPath)
            ? 0
            : Math.Max(0, requestPath.Count(c => c == PathSeparator) - 1);
        return depth == 0 ? CurrentDirectory : string.Concat(Enumerable.Repeat(ParentDirectory, depth));
    }

    private static bool IsRootedOrAbsolute(string value) =>
        value.StartsWith(PathSeparator)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    private static string StripCurrentDirectory(string value)
    {
        if (value == ".") return string.Empty;
        return value.StartsWith(CurrentDirectory, StringComparison.Ordinal) ? value[CurrentDirectory.Length..] : value;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith(PathSeparator) ? value : value + PathSeparator;
}
