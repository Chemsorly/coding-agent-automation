using System.Diagnostics.Metrics;

namespace CodingAgentWebUI.TestUtilities;

/// <summary>
/// A test-only <see cref="IMeterFactory"/> that creates isolated <see cref="Meter"/> instances
/// for each unique name/version pair. Each instance produced lives as long as the factory.
///
/// Mirrors the internal <c>TestMeterFactory</c> in ASP.NET Core (src/Shared/Metrics).
/// Combine with <c>MetricCollector&lt;T&gt;</c> from
/// <c>Microsoft.Extensions.Diagnostics.Metrics.Testing</c> to assert on measurements:
/// <code>
/// var factory = new TestMeterFactory();
/// var collector = new MetricCollector&lt;long&gt;(factory, "CodingAgent.Pipeline", "brain.updates.empty");
/// // ... call production code that uses the meter ...
/// collector.GetMeasurementSnapshot().Should().ContainSingle(m =&gt; m.Value == 1);
/// factory.Dispose();
/// </code>
/// </summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>All meters created by this factory, in creation order.</summary>
    public IReadOnlyList<Meter> Meters => _meters.AsReadOnly();
    private readonly List<Meter> _meters = [];

    /// <inheritdoc/>
    public Meter Create(MeterOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(TestMeterFactory));

        lock (_lock)
        {
            // Return existing meter for same name/version (matches DefaultMeterFactory semantics)
            var existing = _meters.Find(m => m.Name == options.Name && m.Version == options.Version);
            if (existing is not null)
                return existing;

            var meter = new Meter(options.Name, options.Version, [], scope: this);
            _meters.Add(meter);
            return meter;
        }
    }

    /// <summary>Disposes all meters created by this factory.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var meter in _meters)
                meter.Dispose();
            _meters.Clear();
        }
    }
}
