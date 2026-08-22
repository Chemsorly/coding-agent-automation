using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Kubernetes;
using YamlDotNet.Core;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="JobTemplateStore"/> covering all loading paths,
/// lookup, and error handling.
/// </summary>
public sealed class JobTemplateStoreTests
{
    private const string SingleYaml = """
        - labels: dotnet,kiro
          image: agent:latest
          providerType: kiro
          maxConcurrent: 2
        """;

    private const string SingleJson = """
        [{"labels":"dotnet,kiro","image":"agent:latest","providerType":"kiro","maxConcurrent":2}]
        """;

    // ── CreateEmpty ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateEmpty_ReturnsStoreWithNoTemplates()
    {
        var store = JobTemplateStore.CreateEmpty();

        store.GetAllTemplates().Should().BeEmpty();
        store.Resolve("dotnet,kiro").Should().BeNull();
        store.GetMaxConcurrent("dotnet,kiro").Should().Be(0);
    }

    // ── LoadFromJson ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_ValidJson_ResolvesTemplate()
    {
        var store = JobTemplateStore.LoadFromJson(SingleJson);

        var template = store.Resolve("kiro,dotnet"); // order-independent lookup
        template.Should().NotBeNull();
        template!.Image.Should().Be("agent:latest");
        template.ProviderType.Should().Be("kiro");
    }

    [Fact]
    public void LoadFromJson_DuplicateLabels_LastWins()
    {
        const string json = """
            [
              {"labels":"dotnet,kiro","image":"first:1.0","providerType":"kiro","maxConcurrent":1},
              {"labels":"kiro,dotnet","image":"second:2.0","providerType":"kiro","maxConcurrent":1}
            ]
            """;

        var store = JobTemplateStore.LoadFromJson(json);

        store.Resolve("dotnet,kiro")!.Image.Should().Be("second:2.0");
    }

    [Fact]
    public void LoadFromJson_EmptyImage_Throws()
    {
        const string json = """[{"labels":"dotnet,kiro","image":"","providerType":"kiro"}]""";

        var act = () => JobTemplateStore.LoadFromJson(json);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty Image*");
    }

    [Fact]
    public void LoadFromJson_MalformedJson_Throws()
    {
        var act = () => JobTemplateStore.LoadFromJson("not-json-at-all{{{");

        act.Should().Throw<JsonException>();
    }

    // ── LoadFromYaml ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromYaml_MalformedYaml_Throws()
    {
        var act = () => JobTemplateStore.LoadFromYaml(":\t: bad\t: yaml: {{{");

        act.Should().Throw<Exception>(); // YamlException or downstream parse exception
    }

    [Fact]
    public void LoadFromYaml_DuplicateLabels_LastWins()
    {
        const string yaml = """
            - labels: dotnet,kiro
              image: first:1.0
              providerType: kiro
              maxConcurrent: 1
            - labels: kiro,dotnet
              image: second:2.0
              providerType: kiro
              maxConcurrent: 1
            """;

        var store = JobTemplateStore.LoadFromYaml(yaml);

        store.Resolve("dotnet,kiro")!.Image.Should().Be("second:2.0");
    }

    [Fact]
    public void LoadFromYaml_EmptyImage_Throws()
    {
        const string yaml = """
            - labels: dotnet,kiro
              image: ""
              providerType: kiro
            """;

        var act = () => JobTemplateStore.LoadFromYaml(yaml);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty Image*");
    }

    // ── LoadFromFile ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromFile_YamlExtension_LoadsSuccessfully()
    {
        var path = Path.GetTempFileName() + ".yaml";
        try
        {
            File.WriteAllText(path, SingleYaml);
            var store = JobTemplateStore.LoadFromFile(path);
            store.Resolve("dotnet,kiro").Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromFile_YmlExtension_LoadsSuccessfully()
    {
        var path = Path.GetTempFileName() + ".yml";
        try
        {
            File.WriteAllText(path, SingleYaml);
            var store = JobTemplateStore.LoadFromFile(path);
            store.Resolve("dotnet,kiro").Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromFile_JsonExtension_LoadsSuccessfully()
    {
        var path = Path.GetTempFileName() + ".json";
        try
        {
            File.WriteAllText(path, SingleJson);
            var store = JobTemplateStore.LoadFromFile(path);
            store.Resolve("dotnet,kiro").Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromFile_UnknownExtension_DefaultsToYaml()
    {
        var path = Path.GetTempFileName(); // no extension
        try
        {
            File.WriteAllText(path, SingleYaml);
            var store = JobTemplateStore.LoadFromFile(path);
            store.Resolve("dotnet,kiro").Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromFile_NonExistentPath_ThrowsFileNotFound()
    {
        var act = () => JobTemplateStore.LoadFromFile("/nonexistent/path/templates.yaml");

        act.Should().Throw<FileNotFoundException>();
    }

    // ── Resolve ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NonExistentSelector_ReturnsNull()
    {
        var store = JobTemplateStore.LoadFromYaml(SingleYaml);

        store.Resolve("java,opencode").Should().BeNull();
    }

    [Fact]
    public void Resolve_UnnormalizedInput_StillResolvesAfterNormalization()
    {
        var store = JobTemplateStore.LoadFromYaml(SingleYaml);

        // Input has different order — NormalizeLabels should sort them
        store.Resolve("kiro, dotnet").Should().NotBeNull();
    }

    // ── GetMaxConcurrent ─────────────────────────────────────────────────────

    [Fact]
    public void GetMaxConcurrent_ExistingSelector_ReturnsConfiguredValue()
    {
        var store = JobTemplateStore.LoadFromYaml(SingleYaml);

        store.GetMaxConcurrent("dotnet,kiro").Should().Be(2);
    }

    [Fact]
    public void GetMaxConcurrent_NonExistentSelector_ReturnsZero()
    {
        var store = JobTemplateStore.LoadFromYaml(SingleYaml);

        store.GetMaxConcurrent("java,opencode").Should().Be(0);
    }

    // ── GetAllTemplates ──────────────────────────────────────────────────────

    [Fact]
    public void GetAllTemplates_ReturnsAllLoadedTemplates()
    {
        const string yaml = """
            - labels: dotnet,kiro
              image: kiro:latest
              providerType: kiro
            - labels: java,opencode
              image: opencode:latest
              providerType: opencode
            """;

        var store = JobTemplateStore.LoadFromYaml(yaml);

        store.GetAllTemplates().Should().HaveCount(2);
    }

    // ── NormalizeLabels ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null!, "")]
    public void NormalizeLabels_EmptyOrWhitespace_ReturnsEmptyString(string? input, string expected)
    {
        JobTemplateStore.NormalizeLabels(input!).Should().Be(expected);
    }

    [Fact]
    public void NormalizeLabels_SortsLabelsAlphabetically()
    {
        var result = JobTemplateStore.NormalizeLabels("zebra,alpha,middle");
        result.Should().Be("alpha,middle,zebra");
    }

    [Fact]
    public void NormalizeLabels_TrimsWhitespace()
    {
        var result = JobTemplateStore.NormalizeLabels(" dotnet , kiro ");
        result.Should().Be("dotnet,kiro");
    }
}
