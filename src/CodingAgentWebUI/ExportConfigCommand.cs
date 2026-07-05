using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Handles the <c>export-config</c> CLI command. When invoked with
/// <c>dotnet run -- export-config --output /path/to/dir</c>, exports pipeline configuration
/// from the database to JSON files and exits.
/// </summary>
internal static class ExportConfigCommand
{
    /// <summary>
    /// Checks if the <c>export-config</c> CLI command was invoked and executes it.
    /// Returns <c>true</c> if the command was handled (caller should exit), <c>false</c> otherwise.
    /// </summary>
    public static async Task<bool> ExecuteAsync(string[] args)
    {
        if (args.Length < 1 || args[0] != "export-config")
            return false;

        var outputArg = args.FirstOrDefault(a => a.StartsWith("--output="))
            ?? args.FirstOrDefault(a => a == "--output");

        string? outputDir = null;
        if (outputArg is not null && outputArg.StartsWith("--output="))
        {
            outputDir = outputArg["--output=".Length..];
        }
        else if (outputArg == "--output")
        {
            var idx = Array.IndexOf(args, "--output");
            if (idx + 1 < args.Length)
                outputDir = args[idx + 1];
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.Error.WriteLine("Usage: dotnet run -- export-config --output /path/to/dir");
            return true;
        }

        // Initialize minimal Serilog for CLI mode
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
            .CreateLogger();

        var exportConfig = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = DatabaseConnectionResolver.Resolve(exportConfig);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Log.Error("export-config requires Database:Host to be configured");
            return true;
        }

        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<PipelineDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<ConfigExportService>();

#pragma warning disable ASP0000 // Intentional: CLI command uses isolated DI container, not the web host
        await using var sp = services.BuildServiceProvider();
#pragma warning restore ASP0000
        var exportService = sp.GetRequiredService<ConfigExportService>();

        Directory.CreateDirectory(outputDir);
        await exportService.ExportAsync(outputDir, CancellationToken.None);

        Log.Information("Export complete: {OutputDir}", outputDir);
        return true;
    }
}
