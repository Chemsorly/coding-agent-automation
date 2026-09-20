using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the inline validation error banner in AgentCoding.razor (issue #2754).
///
/// The banner at line 147 renders inside the Loop Controls &lt;section class="settings-section"&gt;,
/// NOT inside the &lt;div class="agent-toast-stack"&gt;. These tests verify:
/// - The banner appears when <see cref="ILoopStatusService.ValidationErrors"/> is non-empty.
/// - The banner is structurally inline (inside the section, not in the toast stack).
/// - Each validation error message is individually rendered as a list item.
/// - The banner is absent when <see cref="ILoopStatusService.ValidationErrors"/> is empty.
/// - The toast error (_errorMessage) continues to render inside the toast stack as a regression guard.
/// </summary>
public class AgentCodingValidationBannerTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<ILoopStatusService> _mockLoopStatus = new();

    private static PipelineJobTemplate DefaultTemplate => new()
    {
        Id = "t-1",
        Name = "DotNet Repo",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true
    };

    public AgentCodingValidationBannerTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupStoreMocks();
        SetupProjectStoreMocks();
        SetupConfigClientMocks();

        // Default loop state: inactive, no validation errors (individual tests override as needed)
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(false);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns(string.Empty);
        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());
        _mockLoopStatus.SetupGet(l => l.TemplateStatuses)
            .Returns(new Dictionary<string, ConfigStatusSnapshot>());
        _mockLoopStatus.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateIndex).Returns(0);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateCount).Returns(0);
        _mockLoopStatus.SetupGet(l => l.ProcessedCount).Returns(0);
        _mockLoopStatus.SetupGet(l => l.FailedCount).Returns(0);

        var mockSchedulerClient = new Mock<ISchedulerApiClient>();
        mockSchedulerClient.Setup(c => c.StartLoopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoopStartResultDto(true, null));
        mockSchedulerClient.Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockSchedulerClient.Setup(c => c.ResumeLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<ILoopStatusService>(_mockLoopStatus.Object);
        Services.AddSingleton<ISchedulerApiClient>(mockSchedulerClient.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(_mockProjectStore.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton<IDispatchOrchestrationService>(new Mock<IDispatchOrchestrationService>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
    }

    // ── Test 1: Banner renders inside the loop-controls section (not in toast stack) ──

    [Fact]
    public void AgentCoding_ValidationErrors_RendersInlineInLoopControlsSection()
    {
        // Arrange: set a validation error
        _mockLoopStatus.SetupGet(l => l.ValidationErrors)
            .Returns(new[] { "Template 'X' references non-existent issue provider 'ip-bad'." });

        // Act
        var cut = Render<AgentCoding>();

        // Assert: the banner exists inside a .settings-section (the loop controls section)
        // Using QuerySelector so we get null rather than an exception on no-match
        var loopSection = cut.FindAll("section.settings-section")
            .FirstOrDefault(s => s.QuerySelector(".settings-status.status-error") != null);
        Assert.NotNull(loopSection);
        var bannerInSection = loopSection.QuerySelector(".settings-status.status-error");
        Assert.NotNull(bannerInSection);

        // Assert: the banner is NOT inside the toast stack
        var bannerInToastStack = cut.FindAll(".agent-toast-stack .settings-status.status-error");
        Assert.Empty(bannerInToastStack);
    }

    // ── Test 2: Each validation error renders as a separate list item ─────────

    [Fact]
    public void AgentCoding_ValidationErrors_ShowsEachErrorAsListItem()
    {
        // Arrange: two distinct errors
        _mockLoopStatus.SetupGet(l => l.ValidationErrors)
            .Returns(new[] { "error one", "error two" });

        // Act
        var cut = Render<AgentCoding>();

        // TODO: These whole-markup checks are weaker than the per-list-item assertions below and
        // could pass spuriously if the error strings appear anywhere else on the page (e.g., an
        // ARIA label or debug section). The list-item assertions on their own are sufficient.
        // Consider removing these two lines to avoid masking cases where text appears outside
        // the banner. (Review finding: TestQualityReviewer [WARNING] line ~155)
        Assert.Contains("error one", cut.Markup);
        Assert.Contains("error two", cut.Markup);

        // Assert: they appear as <li> elements inside the banner
        var banner = cut.FindAll("section.settings-section .settings-status.status-error")
            .FirstOrDefault();
        Assert.NotNull(banner);

        var listItems = banner!.QuerySelectorAll("li");
        Assert.Equal(2, listItems.Length);
        Assert.Contains(listItems, li => li.TextContent.Contains("error one"));
        Assert.Contains(listItems, li => li.TextContent.Contains("error two"));
    }

    // ── Test 3: Banner is absent when ValidationErrors is empty ──────────────

    [Fact]
    public void AgentCoding_ValidationErrors_Empty_NoBannerRendered()
    {
        // Arrange: no errors (default, but explicit)
        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());

        // Act
        var cut = Render<AgentCoding>();

        // Assert: no .settings-status.status-error in loop controls section
        // (there's no _errorMessage either since no store throws)
        Assert.Empty(cut.FindAll(".settings-status.status-error"));
    }

    // ── Test 4: Toast error stays inside the toast stack (regression guard) ───

    [Fact]
    public void AgentCoding_ToastError_IsInsideToastStack()
    {
        // Arrange: trigger _errorMessage by making the store throw on load
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        // ValidationErrors is empty — only the toast error should appear
        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());

        // Act
        var cut = Render<AgentCoding>();

        // TODO: This test assumes that making LoadProviderConfigsAsync(ProviderKind.Issue) throw
        // will cause the component to set _errorMessage and render a toast. If the component
        // catches the exception silently (no _errorMessage) or propagates it unhandled to an
        // error boundary, Assert.Single will fail with a confusing "expected 1, got 0" message
        // rather than a clear diagnosis. Consider adding:
        //   Assert.Contains("store unavailable", cut.Markup);
        // before the DOM assertions to confirm the error code path was actually exercised.
        // (Review findings: DotNetSpecialist [WARNING] line ~200, TestQualityReviewer [WARNING] line ~196)

        // Assert: exactly one .settings-status.status-error exists inside .agent-toast-stack.
        // Assert.Single (not Assert.NotEmpty) so that accidentally placing the validation banner
        // inside the toast stack would also fail this test (count would be 2).
        var toastError = cut.FindAll(".agent-toast-stack .settings-status.status-error");
        Assert.Single(toastError);

        // Assert: .settings-status.status-error is NOT inside a .settings-section
        // (the inline validation banner must not be rendered when ValidationErrors is empty)
        var sectionError = cut.FindAll("section.settings-section .settings-status.status-error");
        Assert.Empty(sectionError);
    }

    // ── Test 5: CSS regression guard — .settings-status must not carry position: fixed ──
    //
    // bUnit does not evaluate CSS, so the DOM-structure tests above cannot detect a revert
    // of the CSS split (re-adding `position: fixed` to `.settings-status`). This test reads
    // app.css directly and asserts the class boundary is intact.

    [Fact]
    public void AppCss_SettingsStatus_DoesNotHavePositionFixed_AndToastModifierDoes()
    {
        // Locate app.css relative to the test assembly output directory.
        // The wwwroot folder is published alongside the assembly during build.
        var assemblyDir = Path.GetDirectoryName(typeof(AgentCodingValidationBannerTests).Assembly.Location)!;

        // Walk up from the test output to find wwwroot/css/app.css in the source tree.
        // Under dotnet test, the source project is a sibling of the test project.
        var cssPath = FindAppCss(assemblyDir);
        Assert.True(File.Exists(cssPath), $"Could not locate app.css (searched from {assemblyDir})");

        var css = File.ReadAllText(cssPath);

        // Extract the text of the .settings-status rule (up to the next closing brace).
        // This is a lightweight parse sufficient for a single-line minified CSS rule.
        var settingsStatusRule = ExtractCssRule(css, ".settings-status");
        Assert.NotNull(settingsStatusRule);
        Assert.DoesNotContain("position: fixed", settingsStatusRule,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position:fixed", settingsStatusRule,
            StringComparison.OrdinalIgnoreCase);

        // .settings-status-toast MUST carry position: fixed.
        var toastRule = ExtractCssRule(css, ".settings-status-toast");
        Assert.NotNull(toastRule);
        var hasFixed =
            toastRule!.Contains("position: fixed", StringComparison.OrdinalIgnoreCase) ||
            toastRule.Contains("position:fixed", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasFixed,
            ".settings-status-toast must declare position: fixed so toast overlays continue to work.");
    }

    /// <summary>
    /// Extracts the declaration block of the first CSS rule whose selector exactly matches
    /// <paramref name="selector"/>. Returns null if the selector is not found.
    /// </summary>
    /// <remarks>
    /// TODO: This parser uses a plain substring match and is fragile in two ways:
    /// 1. Prefix collisions — ".settings-status" is a strict prefix of ".settings-status-toast".
    ///    If ".settings-status-toast" appears before ".settings-status" in the file AND the CSS is
    ///    minified (no space before "{"), IndexOf(".settings-status{") will match the toast rule
    ///    first and extract its declaration block instead. The DoesNotContain("position: fixed")
    ///    assertion would then pass vacuously against the wrong rule, masking a CSS regression.
    ///    A more robust approach: use a regex anchored to (?&lt;![\\w-])\.settings-status\s*\{
    ///    (negative lookbehind for word/hyphen characters) so ".settings-status-toast" is not matched.
    /// 2. Single-line extraction — css.IndexOf('}', braceOpen) only finds the first closing brace,
    ///    which is correct for single-line minified rules but would truncate multi-line rule blocks.
    /// (Review findings: DotNetSpecialist [WARNING] line ~233, TestQualityReviewer [WARNING] line ~247)
    /// </remarks>
    private static string? ExtractCssRule(string css, string selector)
    {
        // Find the selector followed immediately by " {" or "{"
        var searchKey = selector + " {";
        var altKey    = selector + "{";

        var idx = css.IndexOf(searchKey, StringComparison.Ordinal);
        if (idx < 0) idx = css.IndexOf(altKey, StringComparison.Ordinal);
        if (idx < 0) return null;

        var braceOpen  = css.IndexOf('{', idx);
        var braceClose = css.IndexOf('}', braceOpen);
        if (braceOpen < 0 || braceClose < 0) return null;

        return css.Substring(braceOpen + 1, braceClose - braceOpen - 1);
    }

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> looking for
    /// <c>src/CodingAgent.Web/wwwroot/css/app.css</c>.
    /// </summary>
    private static string FindAppCss(string startDir)
    {
        var relative = Path.Combine("src", "CodingAgent.Web", "wwwroot", "css", "app.css");
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fallback: try the path as published alongside the test assembly
        return Path.Combine(startDir, "wwwroot", "css", "app.css");
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void SetupStoreMocks()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupProjectStoreMocks()
    {
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = new[] { "t-1" } }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { DefaultTemplate });
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupConfigClientMocks()
    {
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadPipelineConfigAsync(ct));
        _mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadAllTemplatesAsync(ct));
        _mockConfigClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadProjectsAsync(ct));
        _mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadQualityGateConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadReviewerConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}
