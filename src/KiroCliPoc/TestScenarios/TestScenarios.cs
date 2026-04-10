using KiroCliLib.Models;

namespace KiroCliPoc.TestScenarios;

/// <summary>
/// Provides predefined test scenarios for validating Kiro CLI integration.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS
/// </remarks>
public static class TestScenarios
{
    /// <summary>
    /// A simple "Hello World" scenario with a follow-up question.
    /// </summary>
    public static KiroCliLib.Models.ExecutionContext HelloWorld => new()
    {
        Prompts = new List<string>
        {
            "Say 'Hello, World!' and introduce yourself briefly.",
            "What are your main capabilities?",
            "Thank you! That's all for now."
        },
        WorkspaceDirectory = Directory.GetCurrentDirectory(),
        AgentName = "feature-developer"
    };

    /// <summary>
    /// A scenario that asks Kiro to analyze the current directory and provide insights.
    /// </summary>
    public static KiroCliLib.Models.ExecutionContext AnalyzeDirectory => new()
    {
        Prompts = new List<string>
        {
            "Analyze the current directory structure and tell me what type of project this is.",
            "What files are most important in this project?",
            "Are there any potential improvements you'd suggest?"
        },
        WorkspaceDirectory = Directory.GetCurrentDirectory(),
        AgentName = "feature-developer"
    };

    /// <summary>
    /// A scenario that demonstrates file creation with follow-up verification.
    /// </summary>
    public static KiroCliLib.Models.ExecutionContext CreateFile => new()
    {
        Prompts = new List<string>
        {
            "Create a file named 'test.txt' with the content 'Hello from Kiro CLI PoC!'",
            "Did you create the file successfully? Please confirm.",
            "Great! Now delete the test.txt file to clean up."
        },
        WorkspaceDirectory = Directory.GetCurrentDirectory(),
        AgentName = "feature-developer"
    };
}
