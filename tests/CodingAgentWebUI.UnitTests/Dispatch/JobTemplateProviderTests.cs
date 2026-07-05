using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="JobTemplateProvider"/> — loads job templates from JSON,
/// resolves templates by agent selector, and fails fast on misconfiguration.
/// </summary>
public class JobTemplateProviderTests
{
    #region Deserialization

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

        var templates = JsonSerializer.Deserialize<List<JobTemplate>>(json, JobTemplateProvider.JsonOptions);

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

        var templates = JsonSerializer.Deserialize<List<JobTemplate>>(json, JobTemplateProvider.JsonOptions);

        templates.Should().HaveCount(1);
        var t = templates![0];
        t.MaxConcurrent.Should().Be(0);
        t.Resources.Should().BeNull();
        t.PodSecurityContext.Should().BeNull();
        t.NodeSelector.Should().BeNull();
        t.InitContainers.Should().BeNull();
        t.Tolerations.Should().BeNull();
    }

    #endregion

    #region Label Normalization

    [Fact]
    public void NormalizeLabels_UnsortedInput_ReturnsSorted()
    {
        JobTemplateProvider.NormalizeLabels("dotnet10,kiro,dotnet")
            .Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void NormalizeLabels_AlreadySorted_ReturnsUnchanged()
    {
        JobTemplateProvider.NormalizeLabels("dotnet,dotnet10,kiro")
            .Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void NormalizeLabels_Whitespace_IsTrimmed()
    {
        JobTemplateProvider.NormalizeLabels(" kiro , dotnet , dotnet10 ")
            .Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public void NormalizeLabels_EmptyString_ReturnsEmpty()
    {
        JobTemplateProvider.NormalizeLabels("").Should().Be("");
    }

    #endregion

    #region Load & Resolve

    [Fact]
    public void LoadFromJson_MultipleTemplates_AllAccessibleBySelector()
    {
        const string json = """
        [
          { "labels": "kiro,dotnet,dotnet10", "image": "img-dotnet", "providerType": "kiro" },
          { "labels": "kiro,python,python312", "image": "img-python", "providerType": "kiro" }
        ]
        """;

        var provider = JobTemplateProvider.LoadFromJson(json);

        provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("img-dotnet");
        provider.Resolve("kiro,python,python312")!.Image.Should().Be("img-python");
    }

    [Fact]
    public void Resolve_UnsortedSelector_MatchesNormalizedTemplate()
    {
        const string json = """
        [{ "labels": "kiro,dotnet,dotnet10", "image": "img-dotnet", "providerType": "kiro" }]
        """;

        var provider = JobTemplateProvider.LoadFromJson(json);

        // Input is unsorted — should still resolve
        provider.Resolve("dotnet10,kiro,dotnet")!.Image.Should().Be("img-dotnet");
    }

    [Fact]
    public void Resolve_UnknownSelector_ReturnsNull()
    {
        const string json = """
        [{ "labels": "kiro,dotnet,dotnet10", "image": "img-dotnet", "providerType": "kiro" }]
        """;

        var provider = JobTemplateProvider.LoadFromJson(json);
        provider.Resolve("opencode,java,java21").Should().BeNull();
    }

    [Fact]
    public void LoadFromJson_EmptyArray_ProducesEmptyProvider()
    {
        var provider = JobTemplateProvider.LoadFromJson("[]");
        provider.Resolve("anything").Should().BeNull();
        provider.GetAllTemplates().Should().BeEmpty();
    }

    [Fact]
    public void LoadFromJson_MalformedJson_Throws()
    {
        var act = () => JobTemplateProvider.LoadFromJson("not json at all");
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void LoadFromFile_MissingFile_ThrowsFileNotFoundException()
    {
        var act = () => JobTemplateProvider.LoadFromFile("/nonexistent/path/job-templates.json");
        act.Should().Throw<FileNotFoundException>();
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

            var provider = JobTemplateProvider.LoadFromFile(tempFile);
            provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("test-image");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion

    #region GetMaxConcurrent

    [Fact]
    public void GetMaxConcurrent_TemplateWithValue_ReturnsValue()
    {
        const string json = """
        [{ "labels": "kiro,dotnet,dotnet10", "image": "img", "providerType": "kiro", "maxConcurrent": 3 }]
        """;

        var provider = JobTemplateProvider.LoadFromJson(json);
        provider.GetMaxConcurrent("dotnet,dotnet10,kiro").Should().Be(3);
    }

    [Fact]
    public void GetMaxConcurrent_NoTemplate_ReturnsZero()
    {
        var provider = JobTemplateProvider.LoadFromJson("[]");
        provider.GetMaxConcurrent("unknown").Should().Be(0);
    }

    [Fact]
    public void GetMaxConcurrent_ZeroValue_MeansNoLimit()
    {
        const string json = """
        [{ "labels": "kiro,dotnet,dotnet10", "image": "img", "providerType": "kiro", "maxConcurrent": 0 }]
        """;

        var provider = JobTemplateProvider.LoadFromJson(json);
        provider.GetMaxConcurrent("dotnet,dotnet10,kiro").Should().Be(0);
    }

    #endregion

    #region DuplicateLabels

    [Fact]
    public void LoadFromJson_DuplicateLabels_LastWins()
    {
        const string json = """
        [
          { "labels": "kiro,dotnet,dotnet10", "image": "first", "providerType": "kiro" },
          { "labels": "dotnet10,kiro,dotnet", "image": "second", "providerType": "kiro" }
        ]
        """;

        // Both normalize to same key — last entry wins
        var provider = JobTemplateProvider.LoadFromJson(json);
        provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("second");
    }

    #endregion

    #region YAML Support

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

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
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

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
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

        var provider = JobTemplateProvider.LoadFromYaml(yaml);
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

            var provider = JobTemplateProvider.LoadFromFile(yamlFile);
            provider.Resolve("dotnet,dotnet10,kiro")!.Image.Should().Be("test-yaml-image");
        }
        finally
        {
            File.Delete(yamlFile);
        }
    }

    #endregion
}
