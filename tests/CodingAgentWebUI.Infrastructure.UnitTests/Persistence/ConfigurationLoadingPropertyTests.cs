using System.Text.Json;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Property-based tests for generic configuration loading via JsonConfigurationStore.
/// Feature: 018-encapsulation-improvements, Property 6: Generic configuration loading completeness
/// </summary>
public class ConfigurationLoadingPropertyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonSerializerOptions _jsonOptions = PipelineJsonOptions.Default;

    public ConfigurationLoadingPropertyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-loading-pbt-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Property 6a: For any directory with N valid JSON files of type AgentProfile,
    /// LoadAgentProfilesAsync returns exactly N items.
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Property]
    public void LoadAllFromDirectory_ReturnsExactlyN_ValidItems(PositiveInt countSeed)
    {
        // Constrain to reasonable count (1-20 files)
        var validCount = (countSeed.Get % 20) + 1;

        // Use a unique base directory per invocation to avoid cross-test contamination
        var baseDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        var profilesDir = Path.Combine(baseDir, "profiles");
        Directory.CreateDirectory(profilesDir);

        // Write N valid AgentProfile JSON files
        for (var i = 0; i < validCount; i++)
        {
            var profile = new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"Profile-{i}",
                AgentProviderConfigId = Guid.NewGuid().ToString(),
                Enabled = true,
                Priority = i
            };

            var json = JsonSerializer.Serialize(profile, _jsonOptions);
            File.WriteAllText(Path.Combine(profilesDir, $"{profile.Id}.json"), json);
        }

        var store = new JsonConfigurationStore(baseDir);
        var result = store.LoadAgentProfilesAsync(CancellationToken.None).GetAwaiter().GetResult();

        result.Count.Should().Be(validCount);
    }

    /// <summary>
    /// Property 6b: Files that fail deserialization are skipped and do not cause
    /// the entire load to fail. For N valid + M invalid files, returns exactly N items.
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Property]
    public void LoadAllFromDirectory_SkipsInvalidFiles_ReturnsOnlyValid(
        PositiveInt validSeed,
        PositiveInt invalidSeed)
    {
        // Constrain to reasonable counts
        var validCount = (validSeed.Get % 10) + 1;
        var invalidCount = (invalidSeed.Get % 5) + 1;

        // Use a unique base directory per invocation to avoid cross-test contamination
        var baseDir = Path.Combine(_tempDir, Guid.NewGuid().ToString());
        var profilesDir = Path.Combine(baseDir, "profiles");
        Directory.CreateDirectory(profilesDir);

        // Write valid AgentProfile JSON files
        for (var i = 0; i < validCount; i++)
        {
            var profile = new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = $"Valid-{i}",
                AgentProviderConfigId = Guid.NewGuid().ToString(),
                Enabled = true,
                Priority = i
            };

            var json = JsonSerializer.Serialize(profile, _jsonOptions);
            File.WriteAllText(Path.Combine(profilesDir, $"{profile.Id}.json"), json);
        }

        // Write invalid JSON files (malformed JSON that will fail deserialization)
        for (var i = 0; i < invalidCount; i++)
        {
            var invalidContent = "{ this is not valid json !!!";
            File.WriteAllText(
                Path.Combine(profilesDir, $"invalid-{Guid.NewGuid()}.json"),
                invalidContent);
        }

        var store = new JsonConfigurationStore(baseDir);
        var result = store.LoadAgentProfilesAsync(CancellationToken.None).GetAwaiter().GetResult();

        result.Count.Should().Be(validCount);
    }

    /// <summary>
    /// Property 6c: Loading from a non-existent directory returns an empty list (not an exception).
    /// **Validates: Requirements 31.1, 31.2**
    /// </summary>
    [Fact]
    public async Task LoadAllFromDirectory_NonExistentDirectory_ReturnsEmpty()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}");
        var store = new JsonConfigurationStore(nonExistentDir);

        var result = await store.LoadAgentProfilesAsync(CancellationToken.None);

        result.Should().BeEmpty();
    }
}
