using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Components.Layout;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// The sidebar's health section. Database and Redis come from the Pipeline API
/// (<c>/api/ui/health</c>, spec 051); agents from the registry.
/// </summary>
public class SidebarHealthIndicatorsTests : BunitContext
{
    private static readonly string[] DotnetLabels = ["dotnet"];
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly FakeUiHealthClient _health = new();

    private AgentRegistryService CreateRegistry() => new(_mockLogger.Object);

    private AgentRegistryService RegistryWith(params (string Id, AgentStatus? Status)[] agents)
    {
        var registry = CreateRegistry();
        foreach (var (id, status) in agents)
        {
            registry.Register(new AgentRegistrationMessage { AgentId = id, Hostname = $"host-{id}", Labels = DotnetLabels }, $"conn-{id}");
            if (status is { } s)
                registry.TransitionStatus(id, s);
        }
        return registry;
    }

    private IRenderedComponent<SidebarHealthIndicators> RenderSidebar(UiHealthResponse? health = null, AgentRegistryService? registry = null)
    {
        if (health is not null)
            _health.Response = health;
        Services.AddSingleton<IUiHealthClient>(_health);
        Services.AddSingleton<IAgentRegistryService>(registry ?? CreateRegistry());
        return Render<SidebarHealthIndicators>();
    }

    private static string DotOf(IRenderedComponent<SidebarHealthIndicators> cut, string label) =>
        cut.FindAll(".sidebar-health-item").First(i => i.TextContent.Contains(label))
            .QuerySelector(".infra-health-dot")!.ClassName ?? "";

    private static string TooltipOf(IRenderedComponent<SidebarHealthIndicators> cut, string label) =>
        cut.FindAll(".sidebar-health-item").First(i => i.TextContent.Contains(label)).GetAttribute("title")!;

    [Fact]
    public void HidesSection_UntilTheFirstHealthRead_WhenNoAgents()
    {
        _health.Gate = new TaskCompletionSource().Task; // the request never answers

        var cut = RenderSidebar();

        Assert.Empty(cut.Markup.Trim());
        Assert.Equal(1, _health.Calls);
    }

    [Fact]
    public void ShowsDatabaseGreen_WhenTheApiReportsItConnected()
    {
        var cut = RenderSidebar(new UiHealthResponse(DatabaseConnected: true, RedisConnected: null));

        Assert.Contains("dot-healthy", DotOf(cut, "Database"));
        Assert.Equal("Database: Connected", TooltipOf(cut, "Database"));
    }

    [Fact]
    public void ShowsDatabaseRed_WhenTheApiReportsItDisconnected()
    {
        var cut = RenderSidebar(new UiHealthResponse(DatabaseConnected: false, RedisConnected: null));

        Assert.Contains("dot-unhealthy", DotOf(cut, "Database"));
        Assert.Equal("Database: Disconnected", TooltipOf(cut, "Database"));
    }

    [Theory]
    [InlineData(true, "dot-healthy", "Redis: Connected")]
    [InlineData(false, "dot-unhealthy", "Redis: Disconnected")]
    [InlineData(null, "dot-inactive", "Redis: Not configured")]
    public void ShowsRedis_AsTheApiReportsIt(bool? redis, string dot, string tooltip)
    {
        var cut = RenderSidebar(new UiHealthResponse(DatabaseConnected: true, RedisConnected: redis));

        Assert.Contains(dot, DotOf(cut, "Redis"));
        Assert.Equal(tooltip, TooltipOf(cut, "Redis"));
    }

    [Fact]
    public void ShowsDatabaseRed_AndRedisUnknown_WhenTheApiIsUnreachable()
    {
        _health.Failure = new HttpRequestException("connection refused");

        var cut = RenderSidebar();

        Assert.Contains("dot-unhealthy", DotOf(cut, "Database"));
        Assert.Contains("dot-inactive", DotOf(cut, "Redis"));
    }

    [Fact]
    public void ShowsAgentGreenDot_WhenAllConnected()
    {
        var cut = RenderSidebar(registry: RegistryWith(("agent-1", null), ("agent-2", null)));

        Assert.Contains("dot-healthy", DotOf(cut, "Agents"));
        Assert.Contains("Agents: 2", cut.FindAll(".sidebar-health-item").First(i => i.TextContent.Contains("Agents")).TextContent);
    }

    [Fact]
    public void ShowsAgentYellowDot_WhenSomeDisconnected()
    {
        var cut = RenderSidebar(registry: RegistryWith(("agent-1", null), ("agent-2", AgentStatus.Disconnected)));

        Assert.Contains("dot-warning", DotOf(cut, "Agents"));
        Assert.Equal("Agents: 1 connected, 1 disconnected", TooltipOf(cut, "Agents"));
    }

    [Fact]
    public void ShowsAgentRedDot_WhenAllDisconnected()
    {
        var cut = RenderSidebar(registry: RegistryWith(("agent-1", AgentStatus.Disconnected)));

        Assert.Contains("dot-unhealthy", DotOf(cut, "Agents"));
    }

    [Fact]
    public void ShowsAgentInactiveDot_WhenNoAgentIsRegistered()
    {
        var cut = RenderSidebar();

        Assert.Contains("dot-inactive", DotOf(cut, "Agents"));
        Assert.Contains("Agents: 0", cut.FindAll(".sidebar-health-item").First(i => i.TextContent.Contains("Agents")).TextContent);
    }

    [Fact]
    public void ShowsAgents_BeforeTheFirstHealthRead()
    {
        _health.Gate = new TaskCompletionSource().Task;

        var cut = RenderSidebar(registry: RegistryWith(("agent-1", null)));

        Assert.Contains(cut.FindAll(".sidebar-health-item"), i => i.TextContent.Contains("Agents: 1"));
        Assert.DoesNotContain(cut.FindAll(".sidebar-health-item"), i => i.TextContent.Contains("Database"));
    }
}
