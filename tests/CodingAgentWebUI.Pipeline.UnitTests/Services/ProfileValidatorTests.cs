using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ProfileValidatorTests
{
    [Fact]
    public void Validate_ValidProfile_WithLabels_ReturnsSuccess()
    {
        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "DotNet Profile",
            MatchLabels = ["kiro", "dotnet"],
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(profile, []);

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Validate_ValidDefaultProfile_NoExistingDefault_ReturnsSuccess()
    {
        var profile = new AgentProfile
        {
            Id = "default-profile",
            DisplayName = "Default Profile",
            MatchLabels = [],
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(profile, []);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyDisplayName_ReturnsFailure()
    {
        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "",
            MatchLabels = ["kiro"],
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(profile, []);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DisplayName");
    }

    [Fact]
    public void Validate_WhitespaceDisplayName_ReturnsFailure()
    {
        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "   \t  ",
            MatchLabels = ["kiro"],
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(profile, []);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DisplayName");
    }

    [Fact]
    public void Validate_SecondDefaultProfile_ReturnsFailure()
    {
        var existingDefault = new AgentProfile
        {
            Id = "existing-default",
            DisplayName = "Existing Default",
            MatchLabels = [],
            AgentProviderConfigId = "provider-1"
        };

        var newDefault = new AgentProfile
        {
            Id = "new-default",
            DisplayName = "New Default",
            MatchLabels = [],
            AgentProviderConfigId = "provider-2"
        };

        var result = ProfileValidator.Validate(newDefault, [existingDefault]);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("default");
    }

    [Fact]
    public void Validate_SameIdDefaultProfile_AllowsUpdate()
    {
        // Updating an existing default profile (same ID) should be allowed
        var existingDefault = new AgentProfile
        {
            Id = "same-id",
            DisplayName = "Existing Default",
            MatchLabels = [],
            AgentProviderConfigId = "provider-1"
        };

        var updatedDefault = new AgentProfile
        {
            Id = "same-id",
            DisplayName = "Updated Default",
            MatchLabels = [],
            AgentProviderConfigId = "provider-1"
        };

        var result = ProfileValidator.Validate(updatedDefault, [existingDefault]);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DefaultProfile_WithOtherNonDefaultExisting_ReturnsSuccess()
    {
        var existingNonDefault = new AgentProfile
        {
            Id = "non-default",
            DisplayName = "Non-Default",
            MatchLabels = ["kiro", "dotnet"],
            AgentProviderConfigId = "provider-1"
        };

        var newDefault = new AgentProfile
        {
            Id = "new-default",
            DisplayName = "New Default",
            MatchLabels = [],
            AgentProviderConfigId = "provider-2"
        };

        var result = ProfileValidator.Validate(newDefault, [existingNonDefault]);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NullProfile_ThrowsArgumentNullException()
    {
        var act = () => ProfileValidator.Validate(null!, []);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Validate_NullExistingProfiles_ThrowsArgumentNullException()
    {
        var profile = new AgentProfile
        {
            Id = "profile-1",
            DisplayName = "Test",
            MatchLabels = ["kiro"],
            AgentProviderConfigId = "provider-1"
        };

        var act = () => ProfileValidator.Validate(profile, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
