using AwesomeAssertions;
using CodingAgent.Web.Services;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// The boot version comes from the page's <c>getAppVersion</c> at start; a failed read must not stop the
/// browser app from booting, it only leaves the version watch without a version to compare against.
/// </summary>
public class VersionWatchBootTests
{
    [Fact]
    public async Task WhenThePageNamesAVersion_SetBootFromPageAsync_ShouldUseIt()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<string?>("getAppVersion", It.IsAny<object?[]?>())).ReturnsAsync("abc1234");
        var watch = new VersionWatch();

        await watch.SetBootFromPageAsync(js.Object);

        watch.Boot.Should().Be("abc1234");
    }

    [Fact]
    public async Task WhenTheVersionReadFails_SetBootFromPageAsync_ShouldLeaveBootEmpty_AndNotThrow()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<string?>("getAppVersion", It.IsAny<object?[]?>()))
            .ThrowsAsync(new JSException("getAppVersion is not defined"));
        var watch = new VersionWatch();

        await watch.SetBootFromPageAsync(js.Object);

        watch.Boot.Should().BeNull();
        watch.Report("def5678");
        watch.IsOutdated.Should().BeFalse("without a boot version there is nothing to compare against");
    }
}
