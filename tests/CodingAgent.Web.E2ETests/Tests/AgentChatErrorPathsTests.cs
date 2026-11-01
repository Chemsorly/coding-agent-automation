using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Covers the error and role paths of the Agent Chat page that
/// <see cref="AgentChatUiRoundTripTests"/> does not: no profiles, failed options load,
/// refused and failed launch, send failure, keyboard shortcuts, and project scoping.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentChatErrorPathsTests : E2ETestBase
{
    public AgentChatErrorPathsTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared constants ──────────────────────────────────────────────────────

    private const string ProfileId = "chat-edge-profile";
    private const string DefaultModel = "claude-sonnet-test";

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the agent profile the chat page offers (on the <c>kiro,dotnet</c> job template) with
    /// a global default route. Call before <see cref="AgentChatPage.NavigateAsync"/>: the page
    /// loads profiles when it initialises.
    /// </summary>
    private async Task SeedChatProfileAsync()
    {
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = ProfileId,
            DisplayName = "Chat Edge Profile",
            JobTemplate = E2ERouting.Key("kiro", "dotnet"),
            Enabled = true
        }, DefaultModel);
    }

    /// <summary>
    /// Subscribes to <see cref="FakeKubernetesJobClient.ChatJobCreated"/>, starts dispatch
    /// asynchronously, and awaits the first chat job created. Copied from
    /// <c>AgentChatUiRoundTripTests</c> (private there).
    /// </summary>
    private async Task<k8s.Models.V1Job> WaitForFirstChatJobAsync(
        Func<Task> startDispatchAction,
        TimeSpan timeout)
    {
        var jobTcs = new TaskCompletionSource<k8s.Models.V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<k8s.Models.V1Job>? handler = null;
        handler = job =>
        {
            Fixture.K8sClient.ChatJobCreated -= handler;
            jobTcs.TrySetResult(job);
        };
        Fixture.K8sClient.ChatJobCreated += handler;
        try
        {
            await startDispatchAction();
            return await jobTcs.Task.WaitAsync(timeout);
        }
        catch
        {
            Fixture.K8sClient.ChatJobCreated -= handler;
            throw;
        }
    }

    /// <summary>Extracts the <c>caa/chat-session-id</c> label from a K8s job.</summary>
    private static string GetChatSessionId(k8s.Models.V1Job job)
    {
        var labels = job.Metadata?.Labels;
        if (labels is not null && labels.TryGetValue("caa/chat-session-id", out var id) && !string.IsNullOrEmpty(id))
            return id;
        throw new InvalidOperationException("caa/chat-session-id label missing on chat job");
    }

    /// <summary>
    /// Launches a chat pod, connects a fake agent, and waits for the chat header to appear.
    /// The caller is responsible for disposing the returned agent and calling
    /// <see cref="AgentChatPage.EndChatAsync"/> when done.
    /// </summary>
    private async Task<FakeAgentClient> StartChatAsync()
    {
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        var job = await WaitForFirstChatJobAsync(async () =>
        {
            await chatPage.SelectProfileAsync(ProfileId);
            await chatPage.LaunchChatPodAsync();
        }, TimeSpan.FromSeconds(60));

        var chatSessionId = GetChatSessionId(job);
        var agentId = $"chat-edge-{Guid.NewGuid():N}"[..21];
        var fakeAgent = new FakeAgentClient(agentId, "kiro", "dotnet");
        var connectTask = fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, chatSessionId);
        _ = connectTask.ContinueWith(
            t => { /* observe fault */ },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(35));
        await chatPage.WaitForChatHeaderAsync(timeoutMs: 60_000);
        return fakeAgent;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When no enabled profile runs on a job template with a tool, the page explains why and
    /// the Launch button stays disabled.
    /// </summary>
    [Fact]
    public async Task NoProfile_ExplainsWhy_AndLaunchStaysDisabled()
    {
        // No seed — profiles list is empty.
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        await Assertions.Expect(Page.Locator(".form-hint"))
            .ToContainTextAsync("No enabled agent profile runs on a job template with a tool.");
        Assert.True(await chatPage.IsLaunchButtonDisabledAsync());
    }

    /// <summary>
    /// When the chat options endpoint returns 500 the page shows the reload message.
    /// </summary>
    [Fact]
    public async Task OptionsFail_ShowsTheMessage()
    {
        // Route before navigating: OnInitializedAsync fetches options during first render.
        await Page.RouteAsync("**/api/ui/chat/options*",
            route => route.FulfillAsync(new() { Status = 500 }));

        await Page.GotoCockpitPageAsync($"{BaseUrl}/agent-chat");

        await Assertions.Expect(Page.Locator(".agent-detail-warning"))
            .ToContainTextAsync("Could not load the agent profiles and projects. Reload the page.",
                new() { Timeout = 10_000 });
        await Assertions.Expect(Page.Locator("h1")).ToHaveTextAsync("Agent Chat");
    }

    /// <summary>
    /// When the launch POST returns 409 the page shows the API's reason and re-enables the
    /// Launch button (no chat window is opened).
    /// </summary>
    [Fact]
    public async Task LaunchRefused_ShowsTheApiReason_AndLaunchIsAvailableAgain()
    {
        await SeedChatProfileAsync();

        // Route only the POST to /api/ui/chat; let GET /api/ui/chat/options fall through.
        await Page.RouteAsync("**/api/ui/chat", async route =>
        {
            if (route.Request.Method == "POST")
                await route.FulfillAsync(new()
                {
                    Status = 409,
                    Body = "\"No capacity for this profile\"",
                    ContentType = "application/json"
                });
            else
                await route.FallbackAsync();
        });

        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectProfileAsync(ProfileId);
        await chatPage.LaunchChatPodAsync();

        await Assertions.Expect(Page.Locator(".agent-detail-warning"))
            .ToContainTextAsync("No capacity for this profile", new() { Timeout = 10_000 });
        await Assertions.Expect(Page.Locator(".btn-start-chat")).ToBeEnabledAsync();
        await Assertions.Expect(Page.Locator(".chat-window")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// When the launch POST returns 500 the page shows a "Launch failed:" banner.
    /// </summary>
    [Fact]
    public async Task LaunchFails_ShowsLaunchFailed()
    {
        await SeedChatProfileAsync();

        await Page.RouteAsync("**/api/ui/chat", async route =>
        {
            if (route.Request.Method == "POST")
                await route.FulfillAsync(new() { Status = 500 });
            else
                await route.FallbackAsync();
        });

        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();
        await chatPage.SelectProfileAsync(ProfileId);
        await chatPage.LaunchChatPodAsync();

        await Assertions.Expect(Page.Locator(".agent-detail-warning"))
            .ToContainTextAsync("Launch failed:", new() { Timeout = 10_000 });
    }

    /// <summary>
    /// When the send-prompt endpoint returns 500 a system message appears, the input stays
    /// usable, and the next send works end-to-end.
    /// </summary>
    [Fact]
    public async Task SendFails_ShowsASystemMessage_AndTheNextSendWorks()
    {
        await SeedChatProfileAsync();
        await using var agent = await StartChatAsync();

        var chatPage = new AgentChatPage(Page, BaseUrl);

        // Route the prompt endpoint to 500 for the first send.
        await Page.RouteAsync("**/api/ui/chat/*/prompt",
            route => route.FulfillAsync(new() { Status = 500 }));

        await chatPage.SendPromptAsync("first prompt");

        // The user message is added before the HTTP call; the system error appears after.
        await Assertions.Expect(Page.Locator(".chat-message-system"))
            .ToContainTextAsync("Failed to send prompt:", new() { Timeout = 15_000 });
        await Assertions.Expect(Page.Locator(".chat-message-user"))
            .ToContainTextAsync("first prompt");
        await Assertions.Expect(Page.Locator(".chat-input")).ToBeEnabledAsync();

        // The hub never received the prompt, so ChatPromptAssigned is still pending.
        Assert.False(agent.ChatPromptAssigned.Task.IsCompleted);

        // Remove the route and verify the second send reaches the agent.
        await Page.UnrouteAsync("**/api/ui/chat/*/prompt");
        await chatPage.SendPromptAsync("second prompt");
        var prompt = await agent.ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.RespondToChatAsync("second answer");

        Assert.Contains("second prompt", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        var responseText = await chatPage.GetResponseTextAsync(timeoutMs: 15_000);
        Assert.Contains("second answer", responseText, StringComparison.OrdinalIgnoreCase);

        await chatPage.EndChatAsync();
    }

    /// <summary>
    /// Shift+Enter adds a newline in the input (no send); Enter sends the accumulated text.
    /// Mutation check: removing <c>&amp;&amp; !e.ShiftKey</c> from the keydown guard in
    /// <c>AgentChat.razor</c> makes this test fail at "count 0 after Shift+Enter".
    /// </summary>
    [Fact]
    public async Task ShiftEnter_AddsALine_AndEnterSends()
    {
        await SeedChatProfileAsync();
        await using var agent = await StartChatAsync();

        var input = Page.Locator(".chat-input");
        await input.FillAsync("first line");
        await input.PressAsync("Shift+Enter");

        // Shift+Enter must not send — no user message yet.
        await Assertions.Expect(Page.Locator(".chat-message-user")).ToHaveCountAsync(0);

        // The textarea value must contain a newline after "first line".
        var value = await input.InputValueAsync();
        Assert.Contains("first line", value, StringComparison.Ordinal);
        Assert.Contains("\n", value, StringComparison.Ordinal);

        // Type the second line and press Enter to send.
        await input.PressSequentiallyAsync("second line");
        await input.PressAsync("Enter");

        // One user message containing both lines.
        await Assertions.Expect(Page.Locator(".chat-message-user"))
            .ToHaveCountAsync(1, new() { Timeout = 10_000 });
        await Assertions.Expect(Page.Locator(".chat-message-user")).ToContainTextAsync("first line");
        await Assertions.Expect(Page.Locator(".chat-message-user")).ToContainTextAsync("second line");

        // The hub prompt carries both lines.
        var prompt = await agent.ChatPromptAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("first line", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("second line", prompt.Prompt, StringComparison.OrdinalIgnoreCase);

        await new AgentChatPage(Page, BaseUrl).EndChatAsync();
    }

    /// <summary>
    /// A global operator sees "Project (optional):" and a "— No project —" option.
    /// </summary>
    [Fact]
    public async Task GlobalOperatorChoice_ShowsOptionalProject()
    {
        await SeedChatProfileAsync();
        var chatPage = new AgentChatPage(Page, BaseUrl);
        await chatPage.NavigateAsync();

        await Assertions.Expect(Page.Locator("label[for='project-select-k8s']"))
            .ToHaveTextAsync("Project (optional):");
        await Assertions.Expect(
                Page.Locator("#project-select-k8s option:has-text('— No project —')"))
            .ToHaveCountAsync(1);
    }

    /// <summary>
    /// A scoped operator sees only their project pre-selected, and the launch POST body
    /// carries that project ID.
    /// </summary>
    [Fact]
    public async Task ScopedOperator_GetsOnlyItsProject_AndLaunchCarriesIt()
    {
        await SeedChatProfileAsync();

        var browser = await Fixture.GetBrowserAsync();
        await using var aliceContext = await browser.NewContextAsync(); // no pre-signed-in state
        var alicePage = await aliceContext.NewPageAsync();

        // Sign in as alice (operator on E2E Team only).
        // GotoCockpitPageAsync follows the /test/sign-in redirect to /user and waits for the
        // cockpit shell to be interactive, satisfying the architecture nav guard.
        await alicePage.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup));

        // Route the launch POST to 409 so no real pod is started; let options GET fall through.
        await alicePage.RouteAsync("**/api/ui/chat", async route =>
        {
            if (route.Request.Method == "POST")
                await route.FulfillAsync(new()
                {
                    Status = 409,
                    Body = "\"stop here\"",
                    ContentType = "application/json"
                });
            else
                await route.FallbackAsync();
        });

        var aliceChatPage = new AgentChatPage(alicePage, BaseUrl);
        await aliceChatPage.NavigateAsync();

        // Alice sees "Project:" (no "(optional)"), one option for E2E Team, pre-selected.
        await Assertions.Expect(alicePage.Locator("label[for='project-select-k8s']"))
            .ToHaveTextAsync("Project:");
        await Assertions.Expect(alicePage.Locator("#project-select-k8s option"))
            .ToHaveCountAsync(1);
        await Assertions.Expect(alicePage.Locator("#project-select-k8s option"))
            .ToHaveTextAsync("E2E Team");
        await Assertions.Expect(alicePage.Locator("#project-select-k8s"))
            .ToHaveValueAsync(E2ETestSignIn.TeamProjectId);

        // Capture the launch POST, then click.
        var requestTask = alicePage.WaitForRequestAsync(
            r => r.Method == "POST" && r.Url.EndsWith("/api/ui/chat"));
        await aliceChatPage.SelectProfileAsync(ProfileId);
        await alicePage.WaitForFunctionAsync(
            "() => { const b = document.querySelector('.btn-start-chat'); return b && !b.disabled; }",
            null, new() { Timeout = 30_000 });
        await alicePage.ClickAsync(".btn-start-chat");
        var request = await requestTask;

        // The POST body must carry alice's project ID.
        Assert.NotNull(request.PostData);
        Assert.Contains(E2ETestSignIn.TeamProjectId, request.PostData,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A global reader is denied access to the Agent Chat page.
    /// </summary>
    [Fact]
    public async Task GlobalReader_IsDeniedTheChatPage()
    {
        var browser = await Fixture.GetBrowserAsync();
        await using var readerContext = await browser.NewContextAsync(); // no pre-signed-in state
        var readerPage = await readerContext.NewPageAsync();

        // Sign in as the global reader.
        // GotoCockpitPageAsync follows the /test/sign-in redirect to /user and waits for the
        // cockpit shell to be interactive, satisfying the architecture nav guard.
        await readerPage.GotoCockpitPageAsync(E2ETestSignIn.SignInUrl(BaseUrl, E2ETestSignIn.ReaderUser));

        // Use GotoCockpitPageAsync (waits for .cockpit-theme-toggle, not #profile-select).
        // AgentChatPage.NavigateAsync() waits for #profile-select which never renders for a
        // denied user, so it would time out.
        await readerPage.GotoCockpitPageAsync($"{BaseUrl}/agent-chat");

        await Assertions.Expect(readerPage.Locator("[data-testid='access-denied']"))
            .ToBeVisibleAsync();
    }
}
