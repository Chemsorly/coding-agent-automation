using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using Serilog;
using Serilog.Events;

namespace KiroCliPoc;

/// <summary>
/// Main entry point for the Kiro CLI Integration PoC application.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS, DOTNET_PRINCIPLES, ACCEPTANCE_COMPILE_CLEAN
/// </remarks>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure Serilog early for startup logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("Kiro CLI Integration PoC starting");

            // Load configuration
            var config = await ConfigurationManager.LoadAsync();
            ConfigureSerilog(config);

            Log.Information("Configuration loaded successfully");
            Log.Information("=".PadRight(80, '='));
            Log.Information("Persistent Conversation Mode - Resume Flag Approach");
            Log.Information("Type prompts and they'll maintain conversation history via --resume");
            Log.Information("Commands: 'exit'/'quit' to stop, 'clear' to restart conversation");
            Log.Information("=".PadRight(80, '='));

            // Set up callback handler with example callbacks
            var callbackHandler = new CallbackHandler(Log.Logger);
            RegisterCallbacks(callbackHandler);

            // Create orchestrator
            var orchestrator = new KiroCliOrchestrator(config, callbackHandler, Log.Logger);

            // Track workspace and whether we've sent the first prompt
            var workspaceDirectory = Directory.GetCurrentDirectory();
            var isFirstPrompt = true;
            var promptNumber = 0;
            
            while (true)
            {
                Console.Write($"\n[Prompt {++promptNumber}] > ");
                var input = Console.ReadLine();

                // null means stdin closed (e.g. non-interactive Docker container)
                if (input is null)
                {
                    Log.Information("Stdin closed, exiting...");
                    break;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                // Handle commands
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || 
                    input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Exiting...");
                    break;
                }

                if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Clear();
                    promptNumber = 0;
                    isFirstPrompt = true; // Reset conversation
                    
                    Log.Information("=".PadRight(80, '='));
                    Log.Information("Conversation restarted - history cleared");
                    Log.Information("=".PadRight(80, '='));
                    continue;
                }

                // Execute prompt with --resume flag for subsequent prompts
                try
                {
                    var exitCode = await orchestrator.ExecutePromptAsync(
                        input, 
                        workspaceDirectory, 
                        useResume: !isFirstPrompt, 
                        CancellationToken.None);
                    
                    if (exitCode == 0)
                    {
                        isFirstPrompt = false; // Next prompt will use --resume
                    }
                    else
                    {
                        Log.Warning("Prompt execution failed with exit code: {ExitCode}", exitCode);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error executing prompt");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void ConfigureSerilog(KiroCliLib.Configuration.Configuration config)
    {
        var logConfig = new LoggerConfiguration()
            .MinimumLevel.Is(config.LogLevel)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(config.LogFilePath))
        {
            logConfig.WriteTo.File(
                config.LogFilePath,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = logConfig.CreateLogger();
    }

    private static void RegisterCallbacks(CallbackHandler callbackHandler)
    {
        callbackHandler.RegisterOnCompleted(ctx =>
        {
            if (ctx.Files?.Count > 0)
            {
                Log.Information("📁 {FileCount} file(s) changed:", ctx.Files.Count);
                foreach (var file in ctx.Files)
                {
                    Log.Information("   {File}", file);
                }
            }
            if (ctx.TestResults != null)
            {
                Log.Information("🧪 Tests: {Passed}/{Total} passed", 
                    ctx.TestResults.PassedTests, ctx.TestResults.TotalTests);
            }
        });

        callbackHandler.RegisterOnError(ctx =>
        {
            Log.Error("❌ Execution failed: {Message}", ctx.Message);
        });

        callbackHandler.RegisterOnTimeout(ctx =>
        {
            Log.Warning("⏱️  Execution timed out: {Message}", ctx.Message);
        });
    }
}
