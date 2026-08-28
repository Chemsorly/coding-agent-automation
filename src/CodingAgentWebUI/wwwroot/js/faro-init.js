/**
 * Grafana Faro initialization for CodingAgentWebUI.
 *
 * This script is loaded synchronously in <head> so the no-op stub is installed before
 * any other scripts run. CDN bundles are loaded asynchronously via createElement+onload
 * chaining — this avoids the document.write ordering race and doesn't block HTML parsing.
 *
 * IMPORTANT: Because loading is async, Faro does NOT capture errors that occur between
 * this script's execution and the onload callback completing (typically < 1 second on a
 * warm CDN). Blazor initialization errors that fire in that window will be missed.
 * The tradeoff is accepted: non-blocking load > marginally earlier error capture.
 * Faro's window.onerror and unhandledrejection hooks are wired after SDK load regardless.
 *
 * Flow:
 *   1. Install no-op faroApi stub (synchronous, always).
 *   2. Read collector URL from <meta name="faro-collector-url"> (injected by App.razor).
 *   3. If absent/empty → return. Faro disabled (dev/test). No CDN load attempted.
 *   4. If present → async-load SDK bundle, then tracing bundle (onload chain), then init.
 *   5. On successful init → replace stub with real Faro API calls.
 *   6. On CDN failure → stub remains. App unaffected.
 *
 * CDN bundles: @grafana/faro-web-sdk and @grafana/faro-web-tracing v2.8.1
 * SRI hashes are pinned for the exact v2.8.1 artifacts — update both when upgrading.
 * To upgrade: download new bundles, compute sha384 via:
 *   openssl dgst -sha384 -binary <file> | openssl base64 -A
 *
 * NOTE FOR AIR-GAPPED/FIREWALLED DEPLOYMENTS:
 * When Faro__CollectorUrl is set, this script loads two bundles from unpkg.com.
 * In environments without outbound internet access, async CDN loading fails silently
 * (the app continues normally; the stub remains). There is no page-load stall because
 * loading is async. If you need Faro in a firewalled environment, copy the CDN bundles
 * to wwwroot/js/faro/ and update SDK_URL / TRACING_URL below to relative paths.
 */
(function () {
    'use strict';

    var FARO_VERSION = '2.8.1';
    var SDK_URL = 'https://unpkg.com/@grafana/faro-web-sdk@' + FARO_VERSION + '/dist/bundle/faro-web-sdk.iife.js';
    var SDK_SRI = 'sha384-0/sWgM/TFc/aOBqTojW76TtBMJsJToUZI50mii3mcjHi1yv9A+jUDVCwV5BRQ4Wm';
    var TRACING_URL = 'https://unpkg.com/@grafana/faro-web-tracing@' + FARO_VERSION + '/dist/bundle/faro-web-tracing.iife.js';
    var TRACING_SRI = 'sha384-NauzUHGvjWH/QWyybFlULb68c5158zq+w5qTdxvMklLJeLrmTkq9Q6xouTYJGsrK';

    // ── Stub: always available so C# interop calls never throw ReferenceError ─────
    window.faroApi = {
        pushLog: function () { },
        pushError: function () { },
        pushEvent: function () { }
    };

    // ── Read server-injected meta tags ────────────────────────────────────────────
    var collectorMeta = document.querySelector('meta[name="faro-collector-url"]');
    var collectorUrl = collectorMeta && collectorMeta.getAttribute('content');

    if (!collectorUrl) {
        // Dev/test — no collector configured. Stub remains; no CDN load attempted.
        return;
    }

    var versionMeta = document.querySelector('meta[name="app-version"]');
    var appVersion = (versionMeta && versionMeta.getAttribute('content')) || '0.0.0';

    var envMeta = document.querySelector('meta[name="app-environment"]');
    var appEnvironment = (envMeta && envMeta.getAttribute('content')) || 'production';

    // ── initFaro: called after both CDN bundles have loaded ───────────────────────
    function initFaro() {
        if (typeof window.GrafanaFaroWebSdk === 'undefined') {
            // SDK bundle failed to load (offline, blocked, SRI mismatch) — stub remains
            return;
        }

        try {
            var faroInstance = window.GrafanaFaroWebSdk.initializeFaro({
                url: collectorUrl,
                app: {
                    name: 'coding-agent-webui',
                    version: appVersion,
                    environment: appEnvironment
                },
                sessionTracking: {
                    enabled: true,
                    persistent: true
                }
                // Default instrumentations: errors, web vitals, performance, console, xhr/fetch
            });

            // Wire tracing if bundle loaded
            if (typeof window.GrafanaFaroWebTracing !== 'undefined') {
                faroInstance.instrumentations.add(
                    new window.GrafanaFaroWebTracing.TracingInstrumentation()
                );
            }

            // Replace stub with real Faro API
            window.faroApi = {
                pushLog: function (message, level) {
                    try {
                        faroInstance.api.pushLog([message], { level: level || 'info' });
                    } catch (e) { /* swallow */ }
                },
                pushError: function (message, stack) {
                    try {
                        var err = new Error(message);
                        if (stack) err.stack = stack;
                        faroInstance.api.pushError(err);
                    } catch (e) { /* swallow */ }
                },
                pushEvent: function (name, attributes) {
                    try {
                        faroInstance.api.pushEvent(name, attributes || {});
                    } catch (e) { /* swallow */ }
                }
            };

        } catch (e) {
            // Faro init failed — stub remains, app unaffected
            console.warn('[faro-init] Faro initialization failed:', e);
        }
    }

    // ── Async CDN load: SDK first, then tracing (onload chain) ───────────────────
    // Using createElement+appendChild instead of document.write eliminates the
    // document.write ordering race where the third injected inline script could run
    // before the CDN network responses arrived.
    function loadScript(url, sri, onload, onerror) {
        var s = document.createElement('script');
        s.src = url;
        s.integrity = sri;
        s.crossOrigin = 'anonymous';
        s.onload = onload;
        s.onerror = onerror || function () {
            console.warn('[faro-init] Failed to load CDN bundle:', url);
        };
        document.head.appendChild(s);
    }

    // Chain: load SDK → load tracing → init
    loadScript(SDK_URL, SDK_SRI, function () {
        loadScript(TRACING_URL, TRACING_SRI, function () {
            initFaro();
        });
    });
})();
