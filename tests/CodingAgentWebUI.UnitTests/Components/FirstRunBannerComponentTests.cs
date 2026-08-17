using Bunit;
using CodingAgentWebUI.Components.Shared;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="FirstRunBanner"/>.
/// Verifies the show/hide logic:
///   show = noEnabledTemplates AND route != "/settings" AND (notDismissed OR noEnabledTemplates)
/// </summary>
public class FirstRunBannerComponentTests : BunitContext
{
    private readonly Mock<IProjectStore> _projectStore = new();
    private readonly Mock<IKeyValueStore> _keyValueStore = new();

    public FirstRunBannerComponentTests()
    {
        Services.AddSingleton(_projectStore.Object);
        Services.AddSingleton(_keyValueStore.Object);

        // Default: no enabled templates, not dismissed, route is "/"
        _projectStore
            .Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _keyValueStore
            .Setup(s => s.GetAsync("first_run_banner_dismissed", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _keyValueStore
            .Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public void Banner_IsVisible_WhenNoEnabledTemplates_AndNotDismissed_AndNotOnSettings()
    {
        // Arrange: no enabled templates, not dismissed, route is home
        SetCurrentUrl("http://localhost/");

        // Act
        var cut = Render<FirstRunBanner>();

        // Assert: banner markup contains the expected text
        Assert.Contains("No job templates configured", cut.Markup);
    }

    [Fact]
    public void Banner_IsHidden_WhenEnabledTemplatesExist()
    {
        // Arrange: at least one enabled template
        _projectStore
            .Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetCurrentUrl("http://localhost/");

        var cut = Render<FirstRunBanner>();

        Assert.DoesNotContain("No job templates configured", cut.Markup);
    }

    [Fact]
    public void Banner_IsHidden_WhenRouteIsSettings_EvenWithNoTemplates()
    {
        // Arrange: no enabled templates but on /settings
        SetCurrentUrl("http://localhost/settings");

        var cut = Render<FirstRunBanner>();

        Assert.DoesNotContain("No job templates configured", cut.Markup);
    }

    [Fact]
    public async Task Banner_IsHidden_WhenDismissed_AndTemplatesExist()
    {
        // Arrange: templates exist and dismissed flag is set
        _projectStore
            .Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _keyValueStore
            .Setup(s => s.GetAsync("first_run_banner_dismissed", It.IsAny<CancellationToken>()))
            .ReturnsAsync("true");
        SetCurrentUrl("http://localhost/");

        var cut = Render<FirstRunBanner>();

        Assert.DoesNotContain("No job templates configured", cut.Markup);

        // Dismiss button click should have called SetAsync
        await Task.CompletedTask; // satisfy async signature
    }

    [Fact]
    public async Task Banner_StaysVisible_WhenDismissed_ButNoTemplates_BecauseNoTemplatesOverridesDismissal()
    {
        // Spec Req 8.2: "notDismissed OR noEnabledTemplates" means when noEnabledTemplates is true,
        // the third clause is always true — dismissal does not suppress the banner with no templates.
        _keyValueStore
            .Setup(s => s.GetAsync("first_run_banner_dismissed", It.IsAny<CancellationToken>()))
            .ReturnsAsync("true");
        SetCurrentUrl("http://localhost/");

        // noEnabledTemplates = true, notDismissed = false, noEnabledTemplates = true
        // show = true AND true AND (false OR true) = true
        var cut = Render<FirstRunBanner>();

        Assert.Contains("No job templates configured", cut.Markup);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DismissButton_Click_CallsSetAsyncWithDismissedKey()
    {
        // Arrange: banner is visible (no enabled templates, not dismissed, home route)
        SetCurrentUrl("http://localhost/");
        var cut = Render<FirstRunBanner>();
        Assert.Contains("No job templates configured", cut.Markup); // pre-condition: banner is shown

        // Act: click the dismiss button
        var dismissButton = cut.Find(".first-run-banner-dismiss");
        await cut.InvokeAsync(() => dismissButton.Click());

        // Assert: SetAsync was called with the dismiss key and value "true"
        _keyValueStore.Verify(
            s => s.SetAsync("first_run_banner_dismissed", "true", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetCurrentUrl(string url)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        // bUnit's NavigationManager is a fake that supports NavigateTo, changing the current URI
        nav.NavigateTo(url);
    }
}
