using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Span exporter that keeps every exported <see cref="Activity"/> in memory. Add it behind a host's
/// own processors (<c>ConfigureOpenTelemetryTracerProvider(b =&gt; b.AddProcessor(new
/// SimpleActivityExportProcessor(exporter)))</c>) to see exactly what the host's OTLP exporter would
/// send: anything a processor marked as not recorded is skipped by the export processor.
/// </summary>
public sealed class CapturingActivityExporter : BaseExporter<Activity>
{
    private readonly ConcurrentQueue<Activity> _exported = new();

    /// <summary>The spans exported so far, in export order.</summary>
    public IReadOnlyList<Activity> Exported => [.. _exported];

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            _exported.Enqueue(activity);
        return ExportResult.Success;
    }
}
