namespace CodingAgent.Api.Client.Stores;

/// <summary>
/// Spec 051: the hosts that use the API-backed stores (Scheduler, web) only read configuration
/// through them. Settings writes are made by the web host as the signed-in admin, through the
/// Pipeline API's <c>/api/ui/settings</c>; the service surface no longer accepts them with an API key.
/// </summary>
internal static class SettingsWrites
{
    public static Task NotHere(string what) => Task.FromException(Refused(what));

    public static Task<T> NotHere<T>(string what) => Task.FromException<T>(Refused(what));

    private static NotSupportedException Refused(string what) =>
        new($"{what} is not available through the API key; Settings writes go through /api/ui/settings as the signed-in admin.");
}
