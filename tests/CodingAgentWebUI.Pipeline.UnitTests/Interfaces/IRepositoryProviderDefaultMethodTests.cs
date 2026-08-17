using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Interfaces;

/// <summary>
/// Tests for IRepositoryProvider default interface method implementations.
/// </summary>
public class IRepositoryProviderDefaultMethodTests
{
    /// <summary>
    /// Verifies that FormatCloseReference accepts an IssueIdentifier and produces the expected output.
    /// This test locks in the parameter type — if it reverts to string, the explicit IssueIdentifier
    /// construction still compiles (via implicit conversion), but the test documents the intended type.
    /// </summary>
    // Note: Mock<IRepositoryProvider> with CallBase = true may not reliably invoke default interface
    // method implementations (DIM) via Moq — this is undocumented Moq behaviour. If this test becomes
    // flaky, replace the Mock with a concrete minimal stub class that inherits IRepositoryProvider
    // without overriding FormatCloseReference, which guarantees the DIM is invoked.
    [Fact]
    public void FormatCloseReference_AcceptsIssueIdentifier_ReturnsExpectedFormat()
    {
        // Use a partial mock so the default interface implementation runs
        var mock = new Mock<IRepositoryProvider>();
        mock.CallBase = true;

        IssueIdentifier id = "org/repo#42";

        var result = mock.Object.FormatCloseReference(id);

        result.Should().Be("Closes #org/repo#42");
    }

    [Fact]
    public void FormatCloseReference_IssueIdentifierValue_SerializesCorrectly()
    {
        var mock = new Mock<IRepositoryProvider>();
        mock.CallBase = true;

        // Verify the IssueIdentifier.ToString() is used (not struct map format)
        IssueIdentifier id = "42";

        var result = mock.Object.FormatCloseReference(id);

        result.Should().Be("Closes #42");
    }
}
