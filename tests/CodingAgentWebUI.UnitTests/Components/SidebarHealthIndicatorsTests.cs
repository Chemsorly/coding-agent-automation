using Bunit;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI.UnitTests.Components;

// TODO: Missing test coverage (review warnings):
// - Test for periodic timer refresh actually updating the UI when underlying service state changes
// - Test for graceful degradation when injected services throw (verify catch block prevents render failures)
public class SidebarHealthIndicatorsTests : BunitContext
{
    private readonly Mock<ILogger> _mockLogger = new();

    private static InfrastructureHealthService CreateHealthService(
        bool dbConfigured = false,
        bool dbHealthy = true,
        bool redisConfigured = false,
        bool redisConnected = true)
    {
        var services = new ServiceCollection();

        if (dbConfigured)
        {
            var dbHealth = new DatabaseHealthState();
            if (!dbHealthy)
                dbHealth.MarkUnhealthy();
            services.AddSingleton(dbHealth);
        }

        if (redisConfigured)
        {
            var redisMock = new Mock<IConnectionMultiplexer>();
            redisMock.Setup(r => r.IsConnected).Returns(redisConnected);
            services.AddSingleton(redisMock.Object);
        }

        var sp = services.BuildServiceProvider();

        var configData = new Dictionary<string, string?>();
        if (dbConfigured)
            configData["Database:Host"] = "localhost";
        if (redisConfigured)
            configData["SignalR:Redis:ConnectionString"] = "localhost:6379";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new InfrastructureHealthService(sp, configuration, Mock.Of<CodingAgentWebUI.Api.Client.IPipelineApiHealthClient>());
    }

    private AgentRegistryService CreateRegistry()
    {
        return new AgentRegistryService(_mockLogger.Object);
    }

    private void RegisterServices(InfrastructureHealthService? healthService = null, AgentRegistryService? registry = null)
    {
        Services.AddSingleton(healthService ?? CreateHealthService());
        Services.AddSingleton<IAgentRegistryService>(registry ?? CreateRegistry());
    }

    [Fact]
    public void HidesEntireSection_WhenFullLegacyMode()
    {
        // No DB, no Redis, no agents — entire section hidden
        RegisterServices();

        var cut = Render<SidebarHealthIndicators>();

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void ShowsSection_WhenDbConfigured()
    {
        // Spec 045 Task 10 (Req 1.5): DB health removed from monolith.
        // DatabaseConnected is always null — DB items never render.
        // The section is only visible when Redis or agents are configured.
        // When only dbConfigured=true (no Redis, no agents), section is hidden.
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true));

        var cut = Render<SidebarHealthIndicators>();

        // Section is hidden because DatabaseConnected is always null → no DB item,
        // and no Redis or agents registered.
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void ShowsSection_WhenRedisConfigured()
    {
        // Section becomes visible when Redis is configured.
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: true));

        var cut = Render<SidebarHealthIndicators>();

        Assert.NotEmpty(cut.Markup.Trim());
        var section = cut.Find(".sidebar-health");
        Assert.NotNull(section);
    }

    [Fact]
    public void ShowsDbGreenDot_WhenDatabaseConnected()
    {
        // Spec 045 Task 10 (Req 1.5): DB items are never rendered (DatabaseConnected always null).
        // Verify no DB item appears regardless of dbConfigured.
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true));

        var cut = Render<SidebarHealthIndicators>();

        // Section is hidden entirely (no Redis, no agents), so no DB item.
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void ShowsDbRedDot_WhenDatabaseDisconnected()
    {
        // Spec 045 Task 10 (Req 1.5): DB items are never rendered (DatabaseConnected always null).
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: false));

        var cut = Render<SidebarHealthIndicators>();

        // Section hidden; no DB item rendered.
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void HidesDbIndicator_WhenDbNotConfigured()
    {
        // Redis configured but DB not — DB indicator should be hidden
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: true));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        Assert.DoesNotContain(items, i => i.TextContent.Contains("Database"));
    }

    [Fact]
    public void ShowsRedisGreenDot_WhenRedisConnected()
    {
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: true));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        var dot = redisItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-healthy", dot.ClassList.ToString());
    }

    [Fact]
    public void ShowsRedisRedDot_WhenRedisDisconnected()
    {
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: false));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        var dot = redisItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
    }

    [Fact]
    public void ShowsRedisGreyDot_WhenRedisNotConfigured_ButSectionVisible()
    {
        // Spec 045: DB health removed — use an agent to make section visible instead.
        // Redis not configured — grey dot.
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        RegisterServices(CreateHealthService(), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        var dot = redisItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-inactive", dot.ClassList.ToString());
    }

    [Fact]
    public void ShowsAgentGreenDot_WhenAllConnected()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-2",
            Hostname = "host-2",
            Labels = new[] { "dotnet" }
        }, "conn-2");

        RegisterServices(CreateHealthService(dbConfigured: true), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var agentItem = items.First(i => i.TextContent.Contains("Agents"));
        var dot = agentItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-healthy", dot.ClassList.ToString());
        Assert.Contains("Agents: 2", agentItem.TextContent);
    }

    [Fact]
    public void ShowsAgentYellowDot_WhenSomeDisconnected()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-2",
            Hostname = "host-2",
            Labels = new[] { "dotnet" }
        }, "conn-2");
        registry.TransitionStatus("agent-2", AgentStatus.Disconnected);

        RegisterServices(CreateHealthService(dbConfigured: true), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var agentItem = items.First(i => i.TextContent.Contains("Agents"));
        var dot = agentItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-warning", dot.ClassList.ToString());
        Assert.Contains("Agents: 1", agentItem.TextContent);
    }

    [Fact]
    public void ShowsAgentRedDot_WhenAllDisconnected()
    {
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        // Use Redis to keep section visible when all agents are disconnected
        // (section requires connectedCount>0 OR dbStatus!=null OR redisStatus!=null)
        RegisterServices(CreateHealthService(redisConfigured: true), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var agentItem = items.First(i => i.TextContent.Contains("Agents"));
        var dot = agentItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("Agents: 0", agentItem.TextContent);
    }

    [Fact]
    public void ShowsCorrectTooltips()
    {
        // Spec 045: DB health removed. Only Redis tooltip is tested.
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: true));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        Assert.Equal("Redis: Connected", redisItem.GetAttribute("title"));
    }

    [Fact]
    public void ShowsDisconnectedTooltips()
    {
        // Spec 045: DB health removed. Only Redis tooltip is tested.
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: false));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        Assert.Equal("Redis: Disconnected", redisItem.GetAttribute("title"));
    }

    [Fact]
    public void ShowsRedisNotConfiguredTooltip()
    {
        // Spec 045: DB health removed — use agent to make section visible.
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");
        RegisterServices(CreateHealthService(), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        Assert.Equal("Redis: Not configured", redisItem.GetAttribute("title"));
    }

    [Fact]
    public void ShowsAgentsWithRegisteredAgents_EvenWhenNoDbRedis()
    {
        // Only agents registered — section should be visible
        var registry = CreateRegistry();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host-1",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        RegisterServices(registry: registry);

        var cut = Render<SidebarHealthIndicators>();

        Assert.NotEmpty(cut.Markup.Trim());
        var items = cut.FindAll(".sidebar-health-item");
        Assert.Contains(items, i => i.TextContent.Contains("Agents"));
    }

    [Fact]
    public void ShowsAgentInactiveDot_WhenZeroAgentsRegistered_ButSectionVisible()
    {
        // Spec 045: DB health removed — use Redis to make section visible with zero agents.
        var registry = CreateRegistry(); // no agents registered
        RegisterServices(CreateHealthService(redisConfigured: true, redisConnected: true), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var agentItem = items.First(i => i.TextContent.Contains("Agents"));
        var dot = agentItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-inactive", dot.ClassList.ToString());
        Assert.Contains("Agents: 0", agentItem.TextContent);
    }
}
