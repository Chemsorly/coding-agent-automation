using AwesomeAssertions;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DependencyResolver — validates title-based dependency resolution
/// with case-insensitive matching, whitespace trimming, first-registered-wins semantics,
/// and cross-tracker URL emission.
/// </summary>
[Trait("Feature", "027-epic-decomposition-pipeline")]
public class DependencyResolverTests
{
    private readonly ILogger _logger = new Mock<ILogger>().Object;

    // Convenience helpers — all tests use the same provider ID unless they test cross-tracker
    private const string ProviderA = "provider-a";
    private const string ProviderB = "provider-b";
    private const string UrlA1 = "https://github.com/acme/api/issues/1";
    private const string UrlA42 = "https://github.com/acme/api/issues/42";
    private const string UrlB2 = "https://github.com/acme/web/issues/2";
    private const string UrlB3 = "https://github.com/acme/web/issues/3";

    // ─── 1. Basic resolution (same tracker → #N) ────────────────────────────────

    [Fact]
    public void Resolve_RegisteredTitle_SameProvider_ReturnsDependsOnShortForm()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Setup database schema", "42", UrlA42, ProviderA);

        var result = resolver.Resolve(["Setup database schema"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #42");
    }

    [Fact]
    public void Resolve_MultipleDependencies_SameProvider_ReturnsAllShortFormLines()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);
        resolver.Register("Task B", "2", UrlB2, ProviderA);
        resolver.Register("Task C", "3", UrlB3, ProviderA);

        var result = resolver.Resolve(["Task A", "Task C"], ProviderA, _logger);

        result.Should().HaveCount(2);
        result.Should().Contain("Depends on #1");
        result.Should().Contain("Depends on #3");
    }

    [Fact]
    public void Resolve_EmptyDependencyList_ReturnsEmpty()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve([], ProviderA, _logger);

        result.Should().BeEmpty();
    }

    // ─── 2. Cross-tracker — different provider emits full URL (AC2) ─────────────

    [Fact]
    public void Resolve_CrossTracker_DifferentProvider_EmitsFullUrl()
    {
        // AC2: sibling created in ProviderA; sub-issue being created targets ProviderB
        var resolver = new DependencyResolver();
        resolver.Register("API endpoint", "40", "https://github.com/acme/api/issues/40", ProviderA);

        var result = resolver.Resolve(["API endpoint"], ProviderB, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on https://github.com/acme/api/issues/40");
    }

    [Fact]
    public void Resolve_SameTracker_SameProvider_EmitsShortNumber()
    {
        // AC2: sibling created in ProviderA; sub-issue also targets ProviderA → #N
        var resolver = new DependencyResolver();
        resolver.Register("API endpoint", "40", "https://github.com/acme/api/issues/40", ProviderA);

        var result = resolver.Resolve(["API endpoint"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #40");
    }

    [Fact]
    public void Resolve_MixedTrackers_EmitsCorrectFormPerDependency()
    {
        var resolver = new DependencyResolver();
        resolver.Register("API Task", "10", "https://github.com/acme/api/issues/10", ProviderA);
        resolver.Register("Web Task", "20", "https://github.com/acme/web/issues/20", ProviderB);

        // Target is ProviderB: API Task (ProviderA) → URL, Web Task (ProviderB) → #N
        var result = resolver.Resolve(["API Task", "Web Task"], ProviderB, _logger);

        result.Should().HaveCount(2);
        result.Should().Contain("Depends on https://github.com/acme/api/issues/10");
        result.Should().Contain("Depends on #20");
    }

    // ─── 3. Case-insensitive matching ───────────────────────────────────────────

    [Fact]
    public void Resolve_CaseInsensitiveMatch_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Setup Database Schema", "42", UrlA42, ProviderA);

        var result = resolver.Resolve(["setup database schema"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #42");
    }

    [Fact]
    public void Resolve_UpperCaseDependencyTitle_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("add api endpoint", "10", UrlA1, ProviderA);

        var result = resolver.Resolve(["ADD API ENDPOINT"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #10");
    }

    [Fact]
    public void Resolve_MixedCaseRegistrationAndLookup_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Create User Service", "5", "https://github.com/acme/api/issues/5", ProviderA);

        var result = resolver.Resolve(["cReAtE uSeR sErViCe"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #5");
    }

    // ─── 4. Whitespace trimming ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_LeadingWhitespaceInRegistration_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_LeadingWhitespaceInDependencyTitle_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["  Task A  "], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_BothHaveWhitespace_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["  Task A  "], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    // ─── 5. Duplicate titles: first registration wins ───────────────────────────

    [Fact]
    public void Register_DuplicateTitle_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);
        resolver.Register("Task A", "99", "https://github.com/acme/api/issues/99", ProviderA);

        var result = resolver.Resolve(["Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_DuplicateTitleCaseInsensitive_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);
        resolver.Register("TASK A", "99", "https://github.com/acme/api/issues/99", ProviderA);
        resolver.Register("task a", "100", "https://github.com/acme/api/issues/100", ProviderA);

        var result = resolver.Resolve(["Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_DuplicateTitleWithWhitespace_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1", UrlA1, ProviderA);
        resolver.Register("Task A", "99", "https://github.com/acme/api/issues/99", ProviderA);

        var result = resolver.Resolve(["Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    // ─── 6. Unresolved titles: omitted ──────────────────────────────────────────

    [Fact]
    public void Resolve_UnregisteredTitle_Omitted()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["Nonexistent Task"], ProviderA, _logger);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_MixOfResolvedAndUnresolved_ReturnsOnlyResolved()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);
        resolver.Register("Task C", "3", UrlB3, ProviderA);

        var result = resolver.Resolve(["Task A", "Task B", "Task C"], ProviderA, _logger);

        result.Should().HaveCount(2);
        result.Should().Contain("Depends on #1");
        result.Should().Contain("Depends on #3");
    }

    [Fact]
    public void Resolve_ForwardReference_Omitted()
    {
        // Simulates a forward reference: Task B depends on Task C,
        // but Task C hasn't been created yet (not registered).
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["Task C"], ProviderA, _logger);

        result.Should().BeEmpty();
    }

    // ─── 7. Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_WhitespaceOnlyDependencyTitle_Skipped()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["   ", "Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_EmptyStringDependencyTitle_Skipped()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1", UrlA1, ProviderA);

        var result = resolver.Resolve(["", "Task A"], ProviderA, _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_NullTitle_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register(null!, "1", UrlA1, ProviderA);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("title");
    }

    [Fact]
    public void Register_NullIssueNumber_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register("Task A", null!, UrlA1, ProviderA);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("issueNumber");
    }

    [Fact]
    public void Register_NullIssueUrl_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register("Task A", "1", null!, ProviderA);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("issueUrl");
    }

    [Fact]
    public void Register_NullProviderId_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register("Task A", "1", UrlA1, null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("issueProviderId");
    }

    [Fact]
    public void Resolve_NullDependencyTitles_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Resolve(null!, ProviderA, _logger);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("dependencyTitles");
    }

    [Fact]
    public void Resolve_NullTargetProviderId_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Resolve([], null!, _logger);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("targetProviderId");
    }

    [Fact]
    public void Resolve_NullLogger_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Resolve([], ProviderA, null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("logger");
    }

    // ─── 8. Sequential creation simulation ──────────────────────────────────────

    [Fact]
    public void Resolve_SequentialCreation_ResolvesBackwardDependenciesOnly()
    {
        // Simulates sequential issue creation where each issue is registered
        // after creation, and dependencies are resolved before creation.
        var resolver = new DependencyResolver();

        // Issue 1 created (no dependencies)
        resolver.Register("Create models", "100", "https://github.com/acme/api/issues/100", ProviderA);

        // Issue 2 depends on Issue 1 (backward reference — should resolve)
        var deps2 = resolver.Resolve(["Create models"], ProviderA, _logger);
        deps2.Should().ContainSingle().Which.Should().Be("Depends on #100");
        resolver.Register("Add service layer", "101", "https://github.com/acme/api/issues/101", ProviderA);

        // Issue 3 depends on Issue 2 (backward) and Issue 4 (forward — should omit)
        var deps3 = resolver.Resolve(["Add service layer", "Write tests"], ProviderA, _logger);
        deps3.Should().ContainSingle().Which.Should().Be("Depends on #101");
        resolver.Register("Add API endpoint", "102", "https://github.com/acme/api/issues/102", ProviderA);

        // Issue 4 depends on Issue 3 (backward — should resolve)
        var deps4 = resolver.Resolve(["Add API endpoint"], ProviderA, _logger);
        deps4.Should().ContainSingle().Which.Should().Be("Depends on #102");
        resolver.Register("Write tests", "103", "https://github.com/acme/api/issues/103", ProviderA);
    }
}
