using System.Net;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>Fails chosen calls of a fake provider. Thread-safe, because the API host and worker call concurrently.</summary>
public sealed class ProviderFaultPlan
{
    // Factory avoids sharing a single Exception instance across concurrent throws: when times > 1
    // and ThrowIfPlanned is called from two threads simultaneously, the CLR mutates the exception's
    // internal _stackTrace field at the throw site. For the HttpStatusCode overload the factory
    // produces a fresh instance on each call; for the Exception overload the same instance is reused
    // (callers that need strict isolation with times > 1 should pass a factory-creating wrapper).
    private sealed record FaultEntry(Func<Exception> Factory, int Skip, int Times);

    private readonly object _lock = new();
    private readonly Dictionary<string, FaultEntry> _plans = new();

    /// <summary>Fails <paramref name="times"/> calls of a method after letting <paramref name="skip"/> calls pass, with an HttpRequestException carrying the status.</summary>
    public void FailNext(string methodName, HttpStatusCode statusCode = HttpStatusCode.InternalServerError, int times = 1, int skip = 0)
    {
        lock (_lock)
        {
            _plans[methodName] = new FaultEntry(
                () => new HttpRequestException($"Injected {(int)statusCode} from {methodName}", null, statusCode),
                skip, times);
        }
    }

    /// <summary>Same, throwing the given exception.</summary>
    public void FailNext(string methodName, Exception exception, int times = 1, int skip = 0)
    {
        lock (_lock) { _plans[methodName] = new FaultEntry(() => exception, skip, times); }
    }

    internal void ThrowIfPlanned(string methodName)
    {
        Func<Exception>? factory = null;
        lock (_lock)
        {
            if (!_plans.TryGetValue(methodName, out var entry)) return;
            if (entry.Skip > 0)
            {
                _plans[methodName] = entry with { Skip = entry.Skip - 1 };
                return;
            }
            factory = entry.Factory;
            if (entry.Times <= 1) _plans.Remove(methodName);
            else _plans[methodName] = entry with { Times = entry.Times - 1 };
        }
        throw factory();
    }

    public void Reset()
    {
        lock (_lock) { _plans.Clear(); }
    }
}
