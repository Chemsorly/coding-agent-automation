using System.Diagnostics;
using AwesomeAssertions;
using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for <see cref="ChildProcessEnvironment.StripTelemetry"/>.
/// </summary>
[Collection("EnvironmentVariables")]
public class ChildProcessEnvironmentTests
{
    // ── OTEL_ prefix removal ────────────────────────────────────────────

    [Fact]
    public void StripTelemetry_RemovesOtelPrefixedKeys()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        psi.Environment["OTEL_SERVICE_NAME"] = "coding-agent-worker-abc";
        psi.Environment["OTEL_RESOURCE_ATTRIBUTES"] = "k=v";
        psi.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Bearer token";

        ChildProcessEnvironment.StripTelemetry(psi);

        psi.Environment.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT").Should().BeFalse();
        psi.Environment.ContainsKey("OTEL_SERVICE_NAME").Should().BeFalse();
        psi.Environment.ContainsKey("OTEL_RESOURCE_ATTRIBUTES").Should().BeFalse();
        psi.Environment.ContainsKey("OTEL_EXPORTER_OTLP_HEADERS").Should().BeFalse();
    }

    [Fact]
    public void StripTelemetry_RemovesTraceparentAndTracestate()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment["TRACEPARENT"] = "00-abc-def-01";
        psi.Environment["TRACESTATE"] = "vendor=value";

        ChildProcessEnvironment.StripTelemetry(psi);

        psi.Environment.ContainsKey("TRACEPARENT").Should().BeFalse();
        psi.Environment.ContainsKey("TRACESTATE").Should().BeFalse();
    }

    // ── Case-insensitive matching ───────────────────────────────────────

    [Fact]
    public void StripTelemetry_CaseInsensitivePrefix_RemovesLowercaseOtelKey()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment["otel_exporter_otlp_endpoint"] = "http://localhost:4317";
        psi.Environment["Otel_Service_Name"] = "my-service";

        ChildProcessEnvironment.StripTelemetry(psi);

        psi.Environment.ContainsKey("otel_exporter_otlp_endpoint").Should().BeFalse();
        psi.Environment.ContainsKey("Otel_Service_Name").Should().BeFalse();
    }

    [Fact]
    public void StripTelemetry_CaseInsensitiveExactKeys_RemovesLowercaseTraceparentTracestate()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment["traceparent"] = "00-abc-def-01";
        psi.Environment["tracestate"] = "vendor=value";

        ChildProcessEnvironment.StripTelemetry(psi);

        psi.Environment.ContainsKey("traceparent").Should().BeFalse();
        psi.Environment.ContainsKey("tracestate").Should().BeFalse();
    }

    // ── Non-telemetry keys are preserved ───────────────────────────────

    [Fact]
    public void StripTelemetry_DoesNotRemoveNonTelemetryKeys()
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        // Add OTEL keys to prove the strip actually ran (negative-absence guard)
        psi.Environment["OTEL_SERVICE_NAME"] = "worker";
        // Non-telemetry keys that must survive
        psi.Environment["MY_SECRET"] = "shh";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["PATH"] = "/usr/bin";

        ChildProcessEnvironment.StripTelemetry(psi);

        // OTEL key was stripped — proves the method actually ran
        psi.Environment.ContainsKey("OTEL_SERVICE_NAME").Should().BeFalse();

        // Non-telemetry keys survive
        psi.Environment.ContainsKey("MY_SECRET").Should().BeTrue();
        psi.Environment["MY_SECRET"].Should().Be("shh");
        psi.Environment.ContainsKey("GIT_TERMINAL_PROMPT").Should().BeTrue();
        psi.Environment.ContainsKey("PATH").Should().BeTrue();
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void StripTelemetry_EmptyEnvironment_DoesNotThrow()
    {
        // ProcessStartInfo with UseShellExecute=false starts with the parent env populated.
        // We can't create a truly empty one from the outside, but we can verify that a PSI
        // with no OTEL keys in it runs without error.
        var psi = new ProcessStartInfo { UseShellExecute = false };

        // Remove any OTEL keys that were inherited from the test process environment
        var otelKeys = psi.Environment.Keys.Cast<string>()
            .Where(k => k.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in otelKeys) psi.Environment.Remove(k);
        psi.Environment.Remove("TRACEPARENT");
        psi.Environment.Remove("TRACESTATE");

        // Must not throw when there is nothing to strip
        var act = () => ChildProcessEnvironment.StripTelemetry(psi);
        act.Should().NotThrow();
    }

    [Fact]
    public void StripTelemetry_DoesNotMutateParentProcess()
    {
        // Set a key in the parent process environment
        const string key = "OTEL_SERVICE_NAME";
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "test-worker");
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            // Verify the key landed in the PSI copy
            psi.Environment.ContainsKey(key).Should().BeTrue("key must be copied from parent env into PSI");

            ChildProcessEnvironment.StripTelemetry(psi);

            // The PSI copy no longer has the key
            psi.Environment.ContainsKey(key).Should().BeFalse();

            // The parent process environment is unchanged
            Environment.GetEnvironmentVariable(key).Should().Be("test-worker",
                "StripTelemetry must not mutate the parent process environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public void StripTelemetry_NullPsi_ThrowsArgumentNullException()
    {
        var act = () => ChildProcessEnvironment.StripTelemetry(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
