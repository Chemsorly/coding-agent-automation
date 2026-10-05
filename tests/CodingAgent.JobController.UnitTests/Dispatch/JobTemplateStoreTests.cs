using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Kubernetes;
using YamlDotNet.Core;
using Xunit;

namespace CodingAgent.JobController.UnitTests.Dispatch;

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

    [Fact]
    public void LoadFromYaml_ValidYaml_ProducesTemplate()
    {
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "chemsorly/coding-agent:kiro-dotnet10"
          providerType: kiro
          maxConcurrent: 2
          resources:
            requests:
              cpu: "100m"
              memory: "256Mi"
            limits:
              cpu: "2"
              memory: "4Gi"
          podSecurityContext:
            runAsUser: 1000
            fsGroup: 1000
          nodeSelector:
            kubernetes.io/hostname: k8s-deb-1
          initContainers:
            - name: fix-perms
              image: busybox:latest
              command: ["sh", "-c", "chown -R 1000:1000 /data"]
          tolerations:
            - key: agents
              operator: Exists
              effect: NoSchedule
        """;

        var provider = JobTemplateStore.LoadFromYaml(yaml);
        var t = provider.Resolve("dotnet,dotnet10,kiro");

        t.Should().NotBeNull();
        t!.Image.Should().Be("chemsorly/coding-agent:kiro-dotnet10");
        t.MaxConcurrent.Should().Be(2);
        t.Resources!.Requests!["cpu"].Should().Be("100m");
        t.Resources!.Limits!["memory"].Should().Be("4Gi");
        t.NodeSelector!["kubernetes.io/hostname"].Should().Be("k8s-deb-1");
        t.PodSecurityContext.Should().NotBeNull();
        t.InitContainers.Should().NotBeNull();
        t.Tolerations.Should().NotBeNull();
    }

    [Fact]
    public void LoadFromYaml_MinimalYaml_DefaultsCorrectly()
    {
        const string yaml = """
        - labels: "kiro,python,python312"
          image: "chemsorly/coding-agent:kiro-python312"
          providerType: kiro
        """;

        var provider = JobTemplateStore.LoadFromYaml(yaml);
        var t = provider.Resolve("kiro,python,python312");

        t.Should().NotBeNull();
        t!.MaxConcurrent.Should().Be(0);
        t.Resources.Should().BeNull();
        t.PodSecurityContext.Should().BeNull();
        t.NodeSelector.Should().BeNull();
        t.InitContainers.Should().BeNull();
        t.Tolerations.Should().BeNull();
    }

    [Fact]
    public void LoadFromYaml_MultipleTemplates_AllResolvable()
    {
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "img-dotnet"
          providerType: kiro
        - labels: "kiro,python,python312"
          image: "img-python"
          providerType: kiro
        """;

        var provider = JobTemplateStore.LoadFromYaml(yaml);
        provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("img-dotnet");
        provider.Resolve("kiro,python,python312")!.Image.Should().Be("img-python");
    }

    [Fact]
    public void LoadFromFile_YamlExtension_ParsesAsYaml()
    {
        var tempFile = Path.GetTempFileName();
        var yamlFile = Path.ChangeExtension(tempFile, ".yaml");
        File.Move(tempFile, yamlFile);
        try
        {
            File.WriteAllText(yamlFile, """
            - labels: "kiro,dotnet,dotnet10"
              image: "test-yaml-image"
              providerType: kiro
            """);

            var provider = JobTemplateStore.LoadFromFile(yamlFile);
            provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("test-yaml-image");
        }
        finally
        {
            File.Delete(yamlFile);
        }
    }

    [Fact]
    public void LoadFromYaml_PodSecurityContext_NumericFieldsDeserializeAsNumbers()
    {
        // Reproduces production bug: YAML integers in podSecurityContext get serialized
        // as JSON strings by YamlDotNet's Dictionary<string, object> deserialization,
        // causing V1PodSecurityContext deserialization to fail with:
        // "The JSON value could not be converted to System.Nullable`1[System.Int64]. Path: $.fsGroup"
        const string yaml = """
        - labels: "kiro,dotnet,dotnet10"
          image: "chemsorly/coding-agent:kiro-dotnet10"
          providerType: kiro
          podSecurityContext:
            runAsUser: 1000
            runAsGroup: 1000
            fsGroup: 1000
        """;

        var provider = JobTemplateStore.LoadFromYaml(yaml);
        var template = provider.Resolve("dotnet,dotnet10,kiro");

        template.Should().NotBeNull();
        template!.PodSecurityContext.Should().NotBeNull();

        // This is the critical assertion: the JsonElement must contain numbers, not strings.
        // If fsGroup is serialized as "1000" (string) instead of 1000 (number),
        // DeserializeK8s<V1PodSecurityContext> will throw.
        var psc = template.PodSecurityContext!.Value;
        psc.GetProperty("fsGroup").ValueKind.Should().Be(JsonValueKind.Number);
        psc.GetProperty("runAsUser").ValueKind.Should().Be(JsonValueKind.Number);
        psc.GetProperty("runAsGroup").ValueKind.Should().Be(JsonValueKind.Number);
    }

    // ── LoadFromFile ─────────────────────────────────────────────────────────

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

    [Fact]
    public void GetMaxConcurrent_ZeroValue_MeansNoLimit()
    {
        const string json = """
        [{ "labels": "kiro,dotnet,dotnet10", "image": "img", "providerType": "kiro", "maxConcurrent": 0 }]
        """;

        var provider = JobTemplateStore.LoadFromJson(json);
        provider.GetMaxConcurrent("dotnet,dotnet10,kiro").Should().Be(0);
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

    [Fact]
    public void NormalizeLabels_AlreadySorted_ReturnsUnchanged()
    {
        JobTemplateStore.NormalizeLabels("dotnet,dotnet10,kiro")
            .Should().Be("dotnet,dotnet10,kiro");
    }

    // ── Deserialization ─────────────────────────────────────────────

    [Fact]
    public void Deserialize_ValidJson_ProducesTemplate()
    {
        const string json = """
        [
          {
            "labels": "kiro,dotnet,dotnet10",
            "image": "chemsorly/coding-agent:kiro-dotnet10",
            "providerType": "kiro",
            "maxConcurrent": 2,
            "resources": { "requests": { "cpu": "100m", "memory": "256Mi" }, "limits": { "cpu": "2", "memory": "4Gi" } },
            "podSecurityContext": { "runAsUser": 1000, "runAsGroup": 1000, "fsGroup": 1000 },
            "nodeSelector": { "kubernetes.io/hostname": "k8s-deb-1" },
            "initContainers": [
              { "name": "fix-perms", "image": "busybox:latest", "command": ["sh", "-c", "chown -R 1000:1000 /data"] }
            ],
            "tolerations": [
              { "key": "agents", "operator": "Exists", "effect": "NoSchedule" }
            ]
          }
        ]
        """;

        var templates = JsonSerializer.Deserialize<List<JobTemplate>>(json, JobTemplateStore.JsonOptions);

        templates.Should().HaveCount(1);
        var t = templates![0];
        t.Labels.Should().Be("kiro,dotnet,dotnet10");
        t.Image.Should().Be("chemsorly/coding-agent:kiro-dotnet10");
        t.ProviderType.Should().Be("kiro");
        t.MaxConcurrent.Should().Be(2);
        t.Resources.Should().NotBeNull();
        t.Resources!.Requests!["cpu"].Should().Be("100m");
        t.Resources!.Limits!["memory"].Should().Be("4Gi");
        t.PodSecurityContext.Should().NotBeNull();
        t.NodeSelector.Should().ContainKey("kubernetes.io/hostname");
        t.InitContainers.Should().NotBeNull();
        t.InitContainers!.Value.GetArrayLength().Should().Be(1);
        t.Tolerations.Should().NotBeNull();
        t.Tolerations!.Value.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Deserialize_MinimalJson_DefaultsCorrectly()
    {
        const string json = """
        [{ "labels": "kiro,python,python312", "image": "chemsorly/coding-agent:kiro-python312", "providerType": "kiro" }]
        """;

        var templates = JsonSerializer.Deserialize<List<JobTemplate>>(json, JobTemplateStore.JsonOptions);

        templates.Should().HaveCount(1);
        var t = templates![0];
        t.MaxConcurrent.Should().Be(0);
        t.Resources.Should().BeNull();
        t.PodSecurityContext.Should().BeNull();
        t.NodeSelector.Should().BeNull();
        t.InitContainers.Should().BeNull();
        t.Tolerations.Should().BeNull();
    }

    // ── Load & Resolve ──────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_MultipleTemplates_AllAccessibleBySelector()
    {
        const string json = """
        [
          { "labels": "kiro,dotnet,dotnet10", "image": "img-dotnet", "providerType": "kiro" },
          { "labels": "kiro,python,python312", "image": "img-python", "providerType": "kiro" }
        ]
        """;

        var provider = JobTemplateStore.LoadFromJson(json);

        provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("img-dotnet");
        provider.Resolve("kiro,python,python312")!.Image.Should().Be("img-python");
    }

    [Fact]
    public void LoadFromJson_EmptyArray_ProducesEmptyProvider()
    {
        var provider = JobTemplateStore.LoadFromJson("[]");
        provider.Resolve("anything").Should().BeNull();
        provider.GetAllTemplates().Should().BeEmpty();
    }

    [Fact]
    public void LoadFromFile_ValidFile_LoadsTemplates()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            [{ "labels": "kiro,dotnet,dotnet10", "image": "test-image", "providerType": "kiro" }]
            """);

            var provider = JobTemplateStore.LoadFromFile(tempFile);
            provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("test-image");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
