using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Kubernetes;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests.Dispatch;

/// <summary>
/// Null-body edge case tests for <see cref="JobTemplateStore"/> loading methods.
/// Verifies that null serialized inputs produce the correct exception types rather than
/// silently returning an empty or broken store.
/// </summary>
public sealed class JobTemplateStoreNullBodyTests
{
    [Fact]
    public void LoadFromJson_NullJsonLiteral_ThrowsJsonException()
    {
        // "null" is valid JSON that deserializes to a null List<JobTemplate>,
        // which the store detects and wraps in a JsonException.
        var act = () => JobTemplateStore.LoadFromJson("null");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void LoadFromYaml_NullYamlLiteral_ThrowsInvalidOperationException()
    {
        // "~" is the YAML null literal. YamlDotNet deserializes it to a null
        // List<JobTemplateYamlDto>, which LoadFromYaml detects and throws InvalidOperationException.
        var act = () => JobTemplateStore.LoadFromYaml("~");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LoadFromFile_JsonExtension_NullContent_ThrowsJsonException()
    {
        // Writing the JSON null literal to a .json file and loading it via LoadFromFile
        // must propagate the same JsonException as LoadFromJson.
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        try
        {
            File.WriteAllText(path, "null");

            var act = () => JobTemplateStore.LoadFromFile(path);

            act.Should().Throw<JsonException>();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
