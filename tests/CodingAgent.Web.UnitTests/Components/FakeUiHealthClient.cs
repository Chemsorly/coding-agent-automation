using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Stands in for <c>GET /api/ui/health</c> in component tests. Healthy, without Redis, by default.</summary>
internal sealed class FakeUiHealthClient : IUiHealthClient
{
    public UiHealthResponse Response { get; set; } = new(DatabaseConnected: true, RedisConnected: null);

    /// <summary>When set, calls fail with it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, calls wait for it (a request still in flight).</summary>
    public Task? Gate { get; set; }

    public int Calls { get; private set; }

    public async Task<UiHealthResponse> GetAsync(CancellationToken ct = default)
    {
        Calls++;
        if (Gate is not null)
            await Gate;
        if (Failure is not null)
            throw Failure;
        return Response;
    }
}
