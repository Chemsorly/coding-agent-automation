namespace CodingAgent.Web.Services;

/// <summary>
/// Shared by the three drawer services: their dispatch runs in the API (spec 051 2.3c).
/// </summary>
internal static class DrawerDispatchHelper
{
    /// <summary>
    /// Runs a dispatch call and turns its answer into the drawers' result: the API's message on
    /// success and on a refusal, "Could not dispatch — …" when the API cannot be reached.
    /// </summary>
    public static async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchAsync(Func<Task<string>> dispatch)
    {
        try
        {
            return (true, null, await dispatch());
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, null);
        }
        // The call has no caller token, so a cancellation here is the client's timeout.
        catch (Exception ex)
        {
            return (false, $"Could not dispatch — {ex.Message}", null);
        }
    }
}
