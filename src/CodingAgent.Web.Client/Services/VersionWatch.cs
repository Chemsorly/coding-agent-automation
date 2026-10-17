namespace CodingAgent.Web.Services;

/// <summary>
/// Spec 052 Req 5.2, D7: notices that the web host runs a newer build than the one this browser loaded.
/// <see cref="Boot"/> is the page's <c>app-version</c> meta tag, read once at start; fingerprinted assets
/// tie the client files to that page. Every response's <c>X-App-Version</c> is reported; the first one
/// that differs makes the client <see cref="IsOutdated"/>, which shows the banner and reloads the page on
/// the next in-app navigation.
/// </summary>
public sealed class VersionWatch
{
    public string? Boot { get; private set; }

    public bool IsOutdated { get; private set; }

    public event Action? Changed;

    public void SetBoot(string? version) => Boot = string.IsNullOrEmpty(version) ? null : version;

    public void Report(string? version)
    {
        if (Boot is null || IsOutdated || string.IsNullOrEmpty(version) || string.Equals(version, Boot, StringComparison.Ordinal))
            return;
        IsOutdated = true;
        Changed?.Invoke();
    }
}
