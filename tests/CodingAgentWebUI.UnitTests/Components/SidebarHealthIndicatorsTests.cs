using Bunit;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;
using StackExchange.Redis;

namespace CodingAgentWebUI.UnitTests.Components;

// TODO: Missing test coverage (review warnings):
// - Test for "0 agents registered" showing inactive/grey dot when section is visible due to DB/Redis being configured
// - Test for periodic timer refresh actually updating the UI when underlying service state changes
// - Test for graceful degradation when injected services throw (verify catch block prevents render failures)
public class SidebarHealthIndicatorsTests : BunitContext
{
    private readonly Mock<ILogger> _mockLogger = new();

    private InfrastructureHealthService CreateHealthService(
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

        return new InfrastructureHealthService(sp, configuration);
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
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true));

        var cut = Render<SidebarHealthIndicators>();

        Assert.NotEmpty(cut.Markup.Trim());
        var section = cut.Find(".sidebar-health");
        Assert.NotNull(section);
    }

    [Fact]
    public void ShowsDbGreenDot_WhenDatabaseConnected()
    {
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var dbItem = items.First(i => i.TextContent.Contains("Database"));
        var dot = dbItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-healthy", dot.ClassList.ToString());
    }

    [Fact]
    public void ShowsDbRedDot_WhenDatabaseDisconnected()
    {
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: false));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var dbItem = items.First(i => i.TextContent.Contains("Database"));
        var dot = dbItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
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
        // DB configured so section visible, but Redis not configured — grey dot
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true));

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
        Assert.Contains("2/2", agentItem.TextContent);
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
        Assert.Contains("1/2", agentItem.TextContent);
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

        RegisterServices(CreateHealthService(dbConfigured: true), registry);

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var agentItem = items.First(i => i.TextContent.Contains("Agents"));
        var dot = agentItem.QuerySelector(".infra-health-dot")!;
        Assert.Contains("dot-unhealthy", dot.ClassList.ToString());
        Assert.Contains("0/1", agentItem.TextContent);
    }

    [Fact]
    public void ShowsCorrectTooltips()
    {
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: true, redisConfigured: true, redisConnected: true));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var dbItem = items.First(i => i.TextContent.Contains("Database"));
        Assert.Equal("Database: Connected", dbItem.GetAttribute("title"));

        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        Assert.Equal("Redis: Connected", redisItem.GetAttribute("title"));
    }

    [Fact]
    public void ShowsDisconnectedTooltips()
    {
        RegisterServices(CreateHealthService(dbConfigured: true, dbHealthy: false, redisConfigured: true, redisConnected: false));

        var cut = Render<SidebarHealthIndicators>();

        var items = cut.FindAll(".sidebar-health-item");
        var dbItem = items.First(i => i.TextContent.Contains("Database"));
        Assert.Equal("Database: Disconnected", dbItem.GetAttribute("title"));

        var redisItem = items.First(i => i.TextContent.Contains("Redis"));
        Assert.Equal("Redis: Disconnected", redisItem.GetAttribute("title"));
    }

    [Fact]
    public void ShowsRedisNotConfiguredTooltip()
    {
        RegisterServices(CreateHealthService(dbConfigured: true));

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
}
