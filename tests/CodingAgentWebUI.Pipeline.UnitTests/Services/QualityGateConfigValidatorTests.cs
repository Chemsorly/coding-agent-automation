using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class QualityGateConfigValidatorTests
{
    [Fact]
    public void Validate_ValidConfig_WithBothCommands_ReturnsSuccess()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "Full QGC",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            TestCommand = "dotnet",
            TestArguments = ["test"]
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Validate_ValidConfig_CompilationOnly_ReturnsSuccess()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "Compile Only",
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"]
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidConfig_TestOnly_ReturnsSuccess()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "Test Only",
            TestCommand = "dotnet",
            TestArguments = ["test"]
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyDisplayName_ReturnsFailure()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "",
            CompilationCommand = "dotnet"
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DisplayName");
    }

    [Fact]
    public void Validate_WhitespaceDisplayName_ReturnsFailure()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "   ",
            CompilationCommand = "dotnet"
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DisplayName");
    }

    [Fact]
    public void Validate_NoCommands_ReturnsFailure()
    {
        var config = new QualityGateConfiguration
        {
            DisplayName = "Empty QGC",
            CompilationCommand = null,
            TestCommand = null
        };

        var result = QualityGateConfigValidator.Validate(config);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("at least one gate");
    }

    [Fact]
    public void Validate_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => QualityGateConfigValidator.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
