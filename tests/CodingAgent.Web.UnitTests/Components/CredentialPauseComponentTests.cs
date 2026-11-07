using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Components.Shared;
using CodingAgent.Web.Ui;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Spec 056 req 4.1, 4.2 (task 8.4): the paused-credential banner and the credentials card — paused with and without an
/// end time, operator and read-only, nothing paused — and the page-side canary (T4): a detail that carried a key shows
/// only its masked form.
/// </summary>
public sealed class CredentialPauseComponentTests : BunitContext
{
    private const string PausedText = "Kiro paused — quota used up, since 2026-11-02 05:00 UTC (until 2026-12-01 00:00 UTC)";
    private readonly Mock<IUiCredentialsClient> _credentials = new();

    public CredentialPauseComponentTests()
    {
        Services.AddSingleton(_credentials.Object);
    }

    private static UiAgentCredential Kiro(bool paused = true, bool withEnd = true, string? detail = "monthly usage limit reached", int held = 3) =>
        new(AgentCredentials.Kiro, "Kiro", paused,
            paused ? CredentialPauseReasons.QuotaExhausted : null, paused ? CredentialPauseSources.Auto : null,
            paused ? new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero) : null,
            paused && withEnd ? new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) : null, paused ? detail : null, null,
            paused ? (!withEnd ? "Kiro paused — quota used up, since 2026-11-02 05:00 UTC" : PausedText) : null,
            paused ? held : 0, ["Kiro default"]);

    private static UiAgentCredential OpenCode() =>
        new(AgentCredentials.Opencode, "OpenCode", false, null, null, null, null, null, null, null, 0, ["OpenCode java", "OpenCode docs"]);

    private void Returns(params UiAgentCredential[] credentials) =>
        _credentials.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiAgentCredentialsPage(credentials, new Dictionary<string, int>()));

    private void AsOperator() => Services.AddTestAccess(TestAccess.Global(AccessRole.Operator));

    private void AsReadOnly() => Services.AddTestAccess(TestAccess.Global(AccessRole.ReadOnly));

    // ── Banner ───────────────────────────────────────────────────────────────

    [Fact]
    public void Banner_WhenNothingIsPaused_RendersNothing()
    {
        AsOperator();
        Returns(Kiro(paused: false), OpenCode());

        var cut = Render<CredentialPauseBanner>();

        cut.FindAll("[data-testid^=credential-pause-banner]").Should().BeEmpty();
    }

    [Fact]
    public void Banner_WithAnEndTime_ShowsTheTextDetailAndHeldItems()
    {
        AsOperator();
        Returns(Kiro(), OpenCode());

        var cut = Render<CredentialPauseBanner>();

        var banner = cut.Find("[data-testid=credential-pause-banner-kiro]");
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain(PausedText)
            .And.Contain("monthly usage limit reached")
            .And.Contain("3 work items wait for it.");
        cut.FindAll("[data-testid^=credential-pause-banner]").Should().ContainSingle("OpenCode is available");
    }

    [Fact]
    public void Banner_WithoutAnEndTime_SaysSinceOnly()
    {
        AsOperator();
        Returns(Kiro(withEnd: false, held: 1));

        var cut = Render<CredentialPauseBanner>();

        var text = cut.Find("[data-testid=credential-pause-banner-kiro]").TextContent;
        cut.Find("[data-testid=credential-pause-banner-kiro] strong").TextContent.Should().Be("Kiro paused — quota used up, since 2026-11-02 05:00 UTC");
        text.Should().Contain("1 work item waits for it.");
    }

    [Fact]
    public void Banner_ForAnOperator_ResumesTheCredential()
    {
        AsOperator();
        Returns(Kiro());

        var cut = Render<CredentialPauseBanner>();
        cut.Find("[data-testid=credential-resume-kiro]").Click();

        _credentials.Verify(c => c.ResumeAsync(AgentCredentials.Kiro, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Banner_ForAnOperator_PausesUntilANewTime()
    {
        AsOperator();
        Returns(Kiro());

        var cut = Render<CredentialPauseBanner>();
        cut.Find("[data-testid=credential-pause-until-kiro]").Click();
        cut.Find("[data-testid=credential-pause-until]").Change("2026-11-20T08:00");
        cut.Find("[data-testid=credential-pause-note]").Change("waiting for the new plan");
        cut.Find("[data-testid=credential-pause-submit]").Click();

        _credentials.Verify(c => c.PauseAsync(AgentCredentials.Kiro,
            It.Is<DateTimeOffset?>(u => u.HasValue && u.Value.DateTime == new DateTime(2026, 11, 20, 8, 0, 0)),
            "waiting for the new plan", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Banner_ForAReader_HasNoButtons()
    {
        AsReadOnly();
        Returns(Kiro());

        var cut = Render<CredentialPauseBanner>();

        cut.Find("[data-testid=credential-pause-banner-kiro]").TextContent.Should().Contain(PausedText);
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Banner_WhenTheApiIsUnreachable_RendersNothingAndDoesNotThrow()
    {
        AsOperator();
        _credentials.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        var cut = Render<CredentialPauseBanner>();

        cut.FindAll("[data-testid^=credential-pause-banner]").Should().BeEmpty();
    }

    [Fact]
    public void Banner_Canary_T4_ShowsOnlyTheMaskedDetail()
    {
        // The detail the API stores and returns is cleaned (CredentialPauseStore.CleanDetail); the page shows it as is.
        var key = string.Concat("sk-ant-", "api03-", new string('C', 24)); // built at runtime: no key-shaped literal in the repo
        AsOperator();
        Returns(Kiro(detail: CredentialPauseStore.CleanDetail($"401 invalid x-api-key: {key}")));

        var cut = Render<CredentialPauseBanner>();

        cut.Markup.Should().NotContain(key).And.NotContain(new string('C', 24));
        cut.Find("[data-testid=credential-pause-detail]").TextContent.Should().Contain("401 invalid x-api-key");
    }

    // ── Card ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Card_ListsEveryCredential_WithItsStateAndWaitingWork()
    {
        AsOperator();
        Returns(Kiro(), OpenCode());

        var cut = Render<AgentCredentialsCard>();

        cut.Find("[data-testid=credential-row-kiro]").TextContent.Should().Contain("Paused").And.Contain(PausedText).And.Contain("3");
        cut.Find("[data-testid=credential-row-opencode]").TextContent.Should().Contain("Available");
        cut.Markup.Should().NotContain("OpenCode java", "profiles are listed in Settings only");
    }

    [Fact]
    public void Card_InSettings_ListsTheProfilesPerCredential()
    {
        AsOperator();
        Returns(Kiro(), OpenCode());

        var cut = Render<AgentCredentialsCard>(p => p.Add(c => c.ShowProfiles, true));

        cut.Find("[data-testid=credential-row-opencode]").TextContent.Should().Contain("OpenCode java, OpenCode docs");
    }

    [Fact]
    public void Card_ForAnOperator_PausesAnAvailableCredential_UntilResumed()
    {
        AsOperator();
        Returns(Kiro(paused: false), OpenCode());

        var cut = Render<AgentCredentialsCard>();
        cut.Find("[data-testid=credential-card-pause-opencode]").Click();
        cut.Find("[data-testid=credential-pause-submit]").Click();

        _credentials.Verify(c => c.PauseAsync(AgentCredentials.Opencode, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Card_ShowsTheApiRefusal()
    {
        AsOperator();
        Returns(OpenCode());
        _credentials.Setup(c => c.PauseAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The end time must be in the future."));

        var cut = Render<AgentCredentialsCard>();
        cut.Find("[data-testid=credential-card-pause-opencode]").Click();
        cut.Find("[data-testid=credential-pause-submit]").Click();

        cut.Markup.Should().Contain("The end time must be in the future.");
    }

    [Fact]
    public void Card_ForAReader_HasNoButtons()
    {
        AsReadOnly();
        Returns(Kiro(), OpenCode());

        var cut = Render<AgentCredentialsCard>();

        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Card_WhenNoProfileUsesACredential_SaysSo()
    {
        AsOperator();
        Returns();

        var cut = Render<AgentCredentialsCard>();

        cut.Markup.Should().Contain("No agent profile uses a credential yet.");
    }
}
