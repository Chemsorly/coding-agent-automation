using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DependencyResolver — validates title-based dependency resolution
/// with case-insensitive matching, whitespace trimming, and first-registered-wins semantics.
/// </summary>
[Trait("Feature", "027-epic-decomposition-pipeline")]
public class DependencyResolverTests
{
    private readonly ILogger _logger = new Mock<ILogger>().Object;

    // ─── 1. Basic resolution ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_RegisteredTitle_ReturnsDependsOnLine()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Setup database schema", "42");

        var result = resolver.Resolve(["Setup database schema"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #42");
    }

    [Fact]
    public void Resolve_MultipleDependencies_ReturnsAllLines()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");
        resolver.Register("Task B", "2");
        resolver.Register("Task C", "3");

        var result = resolver.Resolve(["Task A", "Task C"], _logger);

        result.Should().HaveCount(2);
        result.Should().Contain("Depends on #1");
        result.Should().Contain("Depends on #3");
    }

    [Fact]
    public void Resolve_EmptyDependencyList_ReturnsEmpty()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");

        var result = resolver.Resolve([], _logger);

        result.Should().BeEmpty();
    }

    // ─── 2. Case-insensitive matching ───────────────────────────────────────────

    [Fact]
    public void Resolve_CaseInsensitiveMatch_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Setup Database Schema", "42");

        var result = resolver.Resolve(["setup database schema"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #42");
    }

    [Fact]
    public void Resolve_UpperCaseDependencyTitle_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("add api endpoint", "10");

        var result = resolver.Resolve(["ADD API ENDPOINT"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #10");
    }

    [Fact]
    public void Resolve_MixedCaseRegistrationAndLookup_ResolvesCorrectly()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Create User Service", "5");

        var result = resolver.Resolve(["cReAtE uSeR sErViCe"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #5");
    }

    // ─── 3. Whitespace trimming ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_LeadingWhitespaceInRegistration_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1");

        var result = resolver.Resolve(["Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_LeadingWhitespaceInDependencyTitle_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");

        var result = resolver.Resolve(["  Task A  "], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_BothHaveWhitespace_TrimsAndMatches()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1");

        var result = resolver.Resolve(["  Task A  "], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    // ─── 4. Duplicate titles: first registration wins ───────────────────────────

    [Fact]
    public void Register_DuplicateTitle_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");
        resolver.Register("Task A", "99");

        var result = resolver.Resolve(["Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_DuplicateTitleCaseInsensitive_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");
        resolver.Register("TASK A", "99");
        resolver.Register("task a", "100");

        var result = resolver.Resolve(["Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_DuplicateTitleWithWhitespace_FirstRegistrationWins()
    {
        var resolver = new DependencyResolver();
        resolver.Register("  Task A  ", "1");
        resolver.Register("Task A", "99");

        var result = resolver.Resolve(["Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    // ─── 5. Unresolved titles: omitted ──────────────────────────────────────────

    [Fact]
    public void Resolve_UnregisteredTitle_Omitted()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");

        var result = resolver.Resolve(["Nonexistent Task"], _logger);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_MixOfResolvedAndUnresolved_ReturnsOnlyResolved()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");
        resolver.Register("Task C", "3");

        var result = resolver.Resolve(["Task A", "Task B", "Task C"], _logger);

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
        resolver.Register("Task A", "1");

        var result = resolver.Resolve(["Task C"], _logger);

        result.Should().BeEmpty();
    }

    // ─── 6. Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_WhitespaceOnlyDependencyTitle_Skipped()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");

        var result = resolver.Resolve(["   ", "Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Resolve_EmptyStringDependencyTitle_Skipped()
    {
        var resolver = new DependencyResolver();
        resolver.Register("Task A", "1");

        var result = resolver.Resolve(["", "Task A"], _logger);

        result.Should().ContainSingle()
            .Which.Should().Be("Depends on #1");
    }

    [Fact]
    public void Register_NullTitle_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register(null!, "1");

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("title");
    }

    [Fact]
    public void Register_NullIssueNumber_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Register("Task A", null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("issueNumber");
    }

    [Fact]
    public void Resolve_NullDependencyTitles_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Resolve(null!, _logger);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("dependencyTitles");
    }

    [Fact]
    public void Resolve_NullLogger_ThrowsArgumentNullException()
    {
        var resolver = new DependencyResolver();

        var act = () => resolver.Resolve([], null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("logger");
    }

    // ─── 7. Sequential creation simulation ──────────────────────────────────────

    [Fact]
    public void Resolve_SequentialCreation_ResolvesBackwardDependenciesOnly()
    {
        // Simulates sequential issue creation where each issue is registered
        // after creation, and dependencies are resolved before creation.
        var resolver = new DependencyResolver();

        // Issue 1 created (no dependencies)
        resolver.Register("Create models", "100");

        // Issue 2 depends on Issue 1 (backward reference — should resolve)
        var deps2 = resolver.Resolve(["Create models"], _logger);
        deps2.Should().ContainSingle().Which.Should().Be("Depends on #100");
        resolver.Register("Add service layer", "101");

        // Issue 3 depends on Issue 2 (backward) and Issue 4 (forward — should omit)
        var deps3 = resolver.Resolve(["Add service layer", "Write tests"], _logger);
        deps3.Should().ContainSingle().Which.Should().Be("Depends on #101");
        resolver.Register("Add API endpoint", "102");

        // Issue 4 depends on Issue 3 (backward — should resolve)
        var deps4 = resolver.Resolve(["Add API endpoint"], _logger);
        deps4.Should().ContainSingle().Which.Should().Be("Depends on #102");
        resolver.Register("Write tests", "103");
    }
}
