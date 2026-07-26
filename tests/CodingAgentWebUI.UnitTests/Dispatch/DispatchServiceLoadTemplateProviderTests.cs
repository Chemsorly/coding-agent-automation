using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="DispatchService.LoadTemplateProvider"/> — verifies the .yaml → .json
/// fallback logic that resolves job template files at startup.
/// </summary>
public sealed class DispatchServiceLoadTemplateProviderTests : IDisposable
{
    private readonly string _tempDir;

    public DispatchServiceLoadTemplateProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dispatch-template-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadTemplateProvider_YamlFileExists_LoadsFromYaml()
    {
        // Arrange: .yaml file present
        var yamlPath = Path.Combine(_tempDir, "templates.yaml");
        File.WriteAllText(yamlPath, """
        - labels: "kiro,dotnet"
          image: "test-yaml-image"
          providerType: kiro
        """);

        var config = BuildConfig(yamlPath);

        // Act
        var store = DispatchService.LoadTemplateProvider(config);

        // Assert
        store.GetAllTemplates().Should().HaveCount(1);
        store.Resolve("dotnet,kiro")!.Image.Should().Be("test-yaml-image");
    }

    [Fact]
    public void LoadTemplateProvider_YamlMissing_JsonExists_FallsBackToJson()
    {
        // Arrange: .yaml configured but only .json file exists
        var yamlPath = Path.Combine(_tempDir, "templates.yaml");
        var jsonPath = Path.Combine(_tempDir, "templates.json");
        File.WriteAllText(jsonPath, """
        [{ "labels": "kiro,dotnet", "image": "test-json-image", "providerType": "kiro" }]
        """);

        var config = BuildConfig(yamlPath);

        // Act
        var store = DispatchService.LoadTemplateProvider(config);

        // Assert
        store.GetAllTemplates().Should().HaveCount(1);
        store.Resolve("dotnet,kiro")!.Image.Should().Be("test-json-image");
    }

    [Fact]
    public void LoadTemplateProvider_NeitherYamlNorJsonExists_ThrowsFileNotFoundException()
    {
        // Arrange: .yaml configured, neither .yaml nor .json exists
        var yamlPath = Path.Combine(_tempDir, "nonexistent.yaml");
        var config = BuildConfig(yamlPath);

        // Act
        var act = () => DispatchService.LoadTemplateProvider(config);

        // Assert
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadTemplateProvider_YmlExtensionMissing_DoesNotFallbackToJson()
    {
        // Arrange: .yml extension (not .yaml) configured + missing — fallback only triggers for .yaml
        var ymlPath = Path.Combine(_tempDir, "templates.yml");
        var jsonPath = Path.Combine(_tempDir, "templates.json");
        File.WriteAllText(jsonPath, """
        [{ "labels": "kiro,dotnet", "image": "test-json-image", "providerType": "kiro" }]
        """);

        var config = BuildConfig(ymlPath);

        // Act
        var act = () => DispatchService.LoadTemplateProvider(config);

        // Assert — .yml does NOT trigger fallback (pre-existing limitation)
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadTemplateProvider_JsonExtensionMissing_DoesNotAttemptFallback()
    {
        // Arrange: .json extension configured + missing — no fallback for non-.yaml paths
        var jsonPath = Path.Combine(_tempDir, "templates.json");
        var config = BuildConfig(jsonPath);

        // Act
        var act = () => DispatchService.LoadTemplateProvider(config);

        // Assert
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadTemplateProvider_NoConfigValue_UsesDefaultPath()
    {
        // Arrange: no WorkDistribution:JobTemplatesPath configured
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act — will throw because the default path doesn't exist in test environment
        var act = () => DispatchService.LoadTemplateProvider(config);

        // Assert — should use DefaultJobTemplatesPath which won't exist
        act.Should().Throw<FileNotFoundException>();
    }

    private static IConfiguration BuildConfig(string templatesPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:JobTemplatesPath"] = templatesPath
            })
            .Build();
    }
}
