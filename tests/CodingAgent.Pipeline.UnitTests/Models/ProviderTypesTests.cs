using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Registration-contract tests for <see cref="ProviderTypes"/> constants.
///
/// The tautological value-lock approach (asserting <c>ProviderTypes.GitHub == "GitHub"</c>)
/// cannot detect the failure mode this refactor was introduced to prevent: a constant value
/// that diverges from the key under which the provider is registered in
/// <see cref="ProviderFactory"/>. These tests verify the real invariant directly.
///
/// Strategy: use reflection to read the registered keys from the factory's private
/// dictionaries and assert that each <see cref="ProviderTypes"/> constant is present.
/// If a constant value changes (e.g. "GitHub" → "Github"), these tests fail; the
/// tautological tests would not.
///
/// TODO: add a completeness test that reflects over <see cref="ProviderTypes"/> public
///       constants and asserts the expected set (GitHub, GitLab, KiroCli). This would
///       catch a new provider type added as a bare string literal in production code
///       without a corresponding constant. Tracked by test-quality review finding (WARNING).
/// </summary>
public class ProviderTypesTests
{
    private static ProviderFactory CreateFactory()
    {
        var mockConfigStore = new Mock<IPipelineConfigStore>();
        mockConfigStore
            .Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        return new ProviderFactory(mockConfigStore.Object);
    }

    private static IReadOnlyCollection<string> GetRegisteredKeys(ProviderFactory factory, string fieldName)
    {
        var field = typeof(ProviderFactory)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        // TODO: if GetField returns null (e.g. the private field was renamed), the Should().NotBeNull()
        //       assertion below fires correctly in single-failure mode, but if the test framework is
        //       configured to collect multiple failures, field!.GetValue(factory) will throw a
        //       NullReferenceException instead of a clean assertion message. Consider replacing with:
        //       field ?? throw new InvalidOperationException($"ProviderFactory has no field '{fieldName}'")
        //       for an unambiguous null guard. Tracked by correctness/dotnet-specialist review finding (WARNING).
        field.Should().NotBeNull($"ProviderFactory should have a private field named '{fieldName}'");

        // The field is Dictionary<string, Func<…>> — retrieve keys via the non-generic IDictionary interface.
        // TODO: the cast to System.Collections.IDictionary assumes the field type implements that interface
        //       (which Dictionary<TKey,TValue> does). If ProviderFactory ever switches to a custom or
        //       non-generic collection type that does not implement IDictionary, this will throw
        //       InvalidCastException instead of a meaningful assertion failure. Prefer casting to a
        //       known concrete generic type (e.g. Dictionary<string, ...>) for type-safety, or add
        //       an explicit type-check with a clear error message. Tracked by correctness review finding (WARNING).
        var dict = (System.Collections.IDictionary)field!.GetValue(factory)!;
        return dict.Keys.Cast<string>().ToList();
    }

    // ── GitHub constant matches issue-factory registration key ───────────────

    [Fact]
    public void ProviderTypes_GitHub_IsRegisteredAsIssueFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_issueFactories");

        keys.Should().Contain(ProviderTypes.GitHub,
            "ProviderFactory registers issue providers under the GitHub key using StringComparer.OrdinalIgnoreCase; " +
            "if ProviderTypes.GitHub diverges from that key, CreateIssueProvider throws NotSupportedException at runtime");
    }

    // ── GitHub constant matches repository-factory registration key ──────────

    [Fact]
    public void ProviderTypes_GitHub_IsRegisteredAsRepositoryFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_repoFactories");

        keys.Should().Contain(ProviderTypes.GitHub,
            "ProviderFactory registers repository providers under the GitHub key; " +
            "ProviderTypes.GitHub must match that registration key");
    }

    // ── GitHub constant matches pipeline-factory registration key ────────────

    [Fact]
    public void ProviderTypes_GitHub_IsRegisteredAsPipelineFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_pipelineFactories");

        keys.Should().Contain(ProviderTypes.GitHub,
            "ProviderFactory registers pipeline providers under the GitHub key; " +
            "ProviderTypes.GitHub must match that registration key");
    }

    // ── GitLab constant matches issue-factory registration key ───────────────

    [Fact]
    public void ProviderTypes_GitLab_IsRegisteredAsIssueFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_issueFactories");

        keys.Should().Contain(ProviderTypes.GitLab,
            "ProviderFactory registers issue providers under the GitLab key; " +
            "ProviderTypes.GitLab must match that registration key");
    }

    // ── GitLab constant matches repository-factory registration key ──────────

    [Fact]
    public void ProviderTypes_GitLab_IsRegisteredAsRepositoryFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_repoFactories");

        keys.Should().Contain(ProviderTypes.GitLab,
            "ProviderFactory registers repository providers under the GitLab key; " +
            "ProviderTypes.GitLab must match that registration key");
    }

    // ── GitLab constant matches pipeline-factory registration key ────────────

    [Fact]
    public void ProviderTypes_GitLab_IsRegisteredAsPipelineFactoryKey()
    {
        var keys = GetRegisteredKeys(CreateFactory(), "_pipelineFactories");

        keys.Should().Contain(ProviderTypes.GitLab,
            "ProviderFactory registers pipeline providers under the GitLab key; " +
            "ProviderTypes.GitLab must match that registration key");
    }

    // TODO: add registration-alignment tests for ProviderTypes.KiroCli covering
    //       AgentProviderFactory (which uses ProviderTypes.KiroCli in a comparison) and any
    //       factory dictionary that registers KiroCli agent providers. Without these tests, a
    //       value change to ProviderTypes.KiroCli (e.g. "KiroCli" → "kiro-cli") would not be
    //       caught at compile or test time — exactly the failure mode this test file was
    //       introduced to prevent for GitHub and GitLab.
    //       Note: KiroCli is NOT registered in ProviderFactory (only GitHub/GitLab are), so the
    //       reflection approach used above does not directly apply; the test should cover
    //       AgentProviderFactory's internal agent-factory dictionary or equivalent surface.
    //       Tracked by dotnet-specialist/test-quality review finding (WARNING).
}
