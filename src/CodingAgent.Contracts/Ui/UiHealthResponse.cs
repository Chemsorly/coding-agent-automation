namespace CodingAgent.Contracts.Ui;

/// <summary>
/// <c>GET /api/ui/health</c>: the infrastructure as the answering Pipeline API replica sees it.
/// <see cref="RedisConnected"/> is null when the API runs without Redis.
/// </summary>
public sealed record UiHealthResponse(bool DatabaseConnected, bool? RedisConnected);
