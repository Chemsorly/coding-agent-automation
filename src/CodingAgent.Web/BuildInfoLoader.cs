using System.Text.Json;
using CodingAgent.Web.Models;

namespace CodingAgent.Web;

/// <summary>Reads <see cref="BuildInfo"/> from <c>build-info.json</c>, which CI writes next to the published app.</summary>
public static class BuildInfoLoader
{
    /// <summary>The build's info, or the local defaults when the file is missing or unreadable.</summary>
    public static BuildInfo Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "build-info.json");
        if (!File.Exists(path))
            return new BuildInfo();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BuildInfo>(json) ?? new BuildInfo();
        }
        catch (JsonException)
        {
            return new BuildInfo();
        }
    }
}
