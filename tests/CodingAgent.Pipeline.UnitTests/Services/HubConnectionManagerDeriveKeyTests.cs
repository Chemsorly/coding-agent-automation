using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for HubConnectionManager.DeriveKey (internal static — HMAC-SHA256 derivation).
/// This method is retained for the non-work-item (chat/model-fetch) legacy path where pods
/// receive the raw master key and derive the per-agent token in-process.
///
/// For work-item pods, derivation no longer occurs inside HubConnectionManager:
/// <c>DispatchLifecycleService</c> pre-computes <c>HMAC-SHA256(masterKey, jobName)</c> and
/// stores it in a per-job K8s Secret. The pod receives this pre-derived value as
/// <c>AGENT_API_KEY</c> and <c>HubConnectionManager</c> uses it directly when
/// <c>keyIsPreDerived = true</c> (see <see cref="HubConnectionManager"/> constructor).
///
/// NOTE: if Agent project adds InternalsVisibleTo for Pipeline.UnitTests in the future,
/// move these to directly call HubConnectionManager.DeriveKey.
/// </summary>
public sealed class HubConnectionManagerDeriveKeyTests
{
    // Access via reflection since InternalsVisibleTo doesn't cover Pipeline.UnitTests
    private static string DeriveKey(string masterKey, string agentId)
    {
        var method = typeof(HubConnectionManager)
            .GetMethod("DeriveKey",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return (string)method!.Invoke(null, [masterKey, agentId])!;
    }

    // ── Legacy DeriveKey behaviour (non-work-item pods) ──────────────────────────────────

    [Fact]
    public void DeriveKey_EmptyAgentId_ReturnsMasterKey()
    {
        var result = DeriveKey("master-secret", "");
        result.Should().Be("master-secret");
    }

    [Fact]
    public void DeriveKey_NonEmptyAgentId_ReturnsDifferentFromMasterKey()
    {
        var result = DeriveKey("master-secret", "agent-1");
        result.Should().NotBe("master-secret");
    }

    [Fact]
    public void DeriveKey_Deterministic_SameInputSameOutput()
    {
        var key1 = DeriveKey("secret", "agent-1");
        var key2 = DeriveKey("secret", "agent-1");
        key1.Should().Be(key2);
    }

    [Fact]
    public void DeriveKey_DifferentAgents_DifferentKeys()
    {
        var key1 = DeriveKey("secret", "agent-1");
        var key2 = DeriveKey("secret", "agent-2");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveKey_DifferentMasterKeys_DifferentKeys()
    {
        var key1 = DeriveKey("secret-a", "agent-1");
        var key2 = DeriveKey("secret-b", "agent-1");
        key1.Should().NotBe(key2);
    }

    [Fact]
    public void DeriveKey_IsHexString_64Chars()
    {
        // HMAC-SHA256 = 32 bytes = 64 hex chars
        var result = DeriveKey("secret", "agent-1");
        result.Should().HaveLength(64);
        result.Should().MatchRegex("^[0-9a-f]+$");
    }

    // ── Pre-derived key path (work-item pods, keyIsPreDerived = true) ──────────────────

    /// <summary>
    /// Verifies that derivation no longer occurs in HubConnectionManager for work-item pods.
    /// When keyIsPreDerived is true, the bearer token equals the supplied apiKey (pre-derived
    /// HMAC-SHA256(masterKey, jobName)), not a second-order derivation of it.
    ///
    /// The test constructs a real HubConnectionManager with keyIsPreDerived=true and extracts
    /// the configured AccessTokenProvider via reflection (the same technique used by
    /// HubConnectionManagerTests.Constructor_TransportOptions_SkipNegotiationAndWebSocketsAreSet
    /// in the Agent.UnitTests project). Invoking the provider confirms the bearer token is the
    /// pre-derived key verbatim, not HMAC(preDerivedKey, jobName).
    /// </summary>
    [Fact]
    public async Task HubConnectionManager_KeyIsPreDerived_True_AccessTokenProvider_ReturnsBearerTokenDirectly_NotReDerivation()
    {
        // Arrange: pre-compute HMAC(masterKey, jobName) — what DispatchLifecycleService stores in the per-job Secret.
        const string masterKey = "master-secret";
        const string jobName = "caa-aabbccdd";
        var preDerivedKey = DeriveKey(masterKey, jobName);
        var doubleDerivation = DeriveKey(preDerivedKey, jobName);

        // Sanity: double-derivation differs from single-derivation — this anchors the assertion below.
        doubleDerivation.Should().NotBe(preDerivedKey,
            "HMAC(HMAC(master,job),job) must differ from HMAC(master,job) — otherwise this test cannot distinguish the two paths");

        var logger = Mock.Of<Serilog.ILogger>();
        await using var manager = new HubConnectionManager(
            orchestratorUrl: "http://localhost",
            agentId: new AgentId(jobName),
            apiKey: preDerivedKey,
            logger: logger,
            keyIsPreDerived: true);

        // Extract AccessTokenProvider from the built HubConnection via reflection.
        // Path: HubConnection._connectionFactory (HttpConnectionFactory)
        //       → HttpConnectionFactory._httpConnectionOptions (HttpConnectionOptions)
        //       → HttpConnectionOptions.AccessTokenProvider (public property)
        var connection = manager.Connection;
        var factoryField = connection.GetType()
            .GetField("_connectionFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        factoryField.Should().NotBeNull("HubConnection must have _connectionFactory (reflection path used by SkipNegotiation test)");

        var factory = factoryField!.GetValue(connection);
        factory.Should().NotBeNull();

        var optionsField = factory!.GetType()
            .GetField("_httpConnectionOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        optionsField.Should().NotBeNull("HttpConnectionFactory must have _httpConnectionOptions");

        var opts = optionsField!.GetValue(factory) as Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions;
        opts.Should().NotBeNull();
        opts!.AccessTokenProvider.Should().NotBeNull("AccessTokenProvider must be set by HubConnectionManager constructor");

        // Act: invoke the AccessTokenProvider to get the actual bearer token.
        var actualToken = await opts.AccessTokenProvider!();

        // Assert: the token must be the pre-derived key, not the double-derived value.
        actualToken.Should().Be(preDerivedKey,
            "when keyIsPreDerived=true, HubConnectionManager must use the received key directly as the bearer token");
        actualToken.Should().NotBe(doubleDerivation,
            "when keyIsPreDerived=true, HubConnectionManager must NOT call DeriveKey again on the already-derived key");
    }

    /// <summary>
    /// Verifies that derivation DOES occur in HubConnectionManager for the legacy (non-work-item) path.
    /// When keyIsPreDerived is false (AGENT_API_KEY_FILE mount), the bearer token is
    /// HMAC(masterKey, agentId) — not the raw master key.
    /// </summary>
    [Fact]
    public async Task HubConnectionManager_KeyIsPreDerived_False_AccessTokenProvider_ReturnsDerivedKey()
    {
        const string masterKey = "master-secret";
        const string jobName = "caa-aabbccdd";
        var expectedDerivedKey = DeriveKey(masterKey, jobName);

        var logger = Mock.Of<Serilog.ILogger>();
        await using var manager = new HubConnectionManager(
            orchestratorUrl: "http://localhost",
            agentId: new AgentId(jobName),
            apiKey: masterKey,
            logger: logger,
            keyIsPreDerived: false);

        var connection = manager.Connection;
        var factoryField = connection.GetType()
            .GetField("_connectionFactory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var factory = factoryField!.GetValue(connection);
        // TODO: Add intermediate null guards with descriptive failure messages for `factory` and
        // `optionsField`, similar to the `factoryField.Should().NotBeNull(...)` guard in the
        // keyIsPreDerived=true test above. If a SignalR library upgrade renames an internal field,
        // `factory!.GetType()` will null-dereference rather than failing with a clear message about
        // which field was not found. Example: factory.Should().NotBeNull("HttpConnectionFactory must
        // be accessible via _connectionFactory reflection path"); Similarly for optionsField.
        var optionsField = factory!.GetType()
            .GetField("_httpConnectionOptions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var opts = optionsField!.GetValue(factory) as Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions;

        var actualToken = await opts!.AccessTokenProvider!();

        actualToken.Should().Be(expectedDerivedKey,
            "when keyIsPreDerived=false (legacy path), HubConnectionManager must derive HMAC(masterKey, agentId) in-process");
        actualToken.Should().NotBe(masterKey,
            "the raw master key must never be used as the bearer token directly");
    }

    /// <summary>
    /// Verifies the security invariant: a work-item pod (holding only its pre-derived key)
    [Fact]
    public void PreDerivedKey_CannotBeUsedToImpersonateDifferentPod()
    {
        const string masterKey = "master-secret";
        const string podA = "caa-aabbccdd";
        const string podB = "caa-eeffgghh";

        // Pod A's credential
        var podAKey = DeriveKey(masterKey, podA);

        // What the server expects for pod B
        var expectedForPodB = DeriveKey(masterKey, podB);

        // Pod A cannot forge pod B's credential
        podAKey.Should().NotBe(expectedForPodB,
            "HMAC(master, podA) ≠ HMAC(master, podB) — per-pod isolation holds");
    }
}
