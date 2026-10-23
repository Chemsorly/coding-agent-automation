using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="ProviderUnavailableException.IsPermanentProviderFailure"/>.
/// No test existed for this method before; these tests also cover the new
/// <see cref="AgentErrorCategory.ProviderBillingLimit"/> value (issue #3860).
/// </summary>
public class ProviderUnavailableExceptionTests
{
    [Theory]
    [InlineData(AgentErrorCategory.PermanentAuthFailure)]
    [InlineData(AgentErrorCategory.ProviderMisconfigured)]
    [InlineData(AgentErrorCategory.ProviderBillingLimit)]
    public void IsPermanentProviderFailure_PermanentCategories_ReturnsTrue(AgentErrorCategory category)
    {
        ProviderUnavailableException.IsPermanentProviderFailure(category).Should().BeTrue(
            $"{category} is permanent and should abort the run immediately");
    }

    [Theory]
    [InlineData(AgentErrorCategory.ProviderRateLimit)]
    [InlineData(AgentErrorCategory.ProviderOverload)]
    [InlineData(AgentErrorCategory.None)]
    public void IsPermanentProviderFailure_TransientOrNoneCategories_ReturnsFalse(AgentErrorCategory category)
    {
        ProviderUnavailableException.IsPermanentProviderFailure(category).Should().BeFalse(
            $"{category} is transient or unclassified and should not abort the run");
    }
}
