using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using CodingAgent.Web.Components.Shared;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Spec 052 Req 5.2: the non-blocking "new version" banner.</summary>
public class NewVersionBannerTests : BunitContext
{
    private readonly VersionWatch _version = new();

    public NewVersionBannerTests()
    {
        _version.SetBoot("aaaaaaa");
        Services.AddSingleton(_version);
    }

    [Fact]
    public void SameBuild_ShowsNothing()
    {
        var cut = Render<NewVersionBanner>();

        _version.Report("aaaaaaa");

        cut.FindAll("[data-testid=new-version-banner]").Should().BeEmpty();
    }

    [Fact]
    public void NewerBuild_ShowsTheBanner_AndReloadLoadsThePageAgain()
    {
        var cut = Render<NewVersionBanner>();

        cut.InvokeAsync(() => _version.Report("bbbbbbb"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=new-version-banner]").TextContent.Should().Contain("A new version is available"));
        cut.Find("[data-testid=new-version-reload]").Click();
        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        nav.History.First().Options.ForceLoad.Should().BeTrue();
    }
}
