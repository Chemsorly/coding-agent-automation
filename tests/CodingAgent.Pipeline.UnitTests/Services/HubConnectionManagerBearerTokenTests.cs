using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the bearer token <see cref="HubConnectionManager"/> sends. Every agent Job receives
/// its own key, <c>HMAC-SHA256(masterKey, jobName)</c>, from a per-Job K8s Secret created by
/// <c>DispatchLifecycleService</c>; the pod never sees the master key and must not derive again.
/// </summary>
public sealed class HubConnectionManagerBearerTokenTests
{
    /// <summary>
    /// The bearer token equals the supplied key, not a second-order derivation of it.
    ///
    /// The test constructs a real HubConnectionManager and extracts the configured
    /// AccessTokenProvider via reflection (the same technique used by
    /// HubConnectionManagerTests.Constructor_TransportOptions_SkipNegotiationAndWebSocketsAreSet
    /// in the Agent.UnitTests project).
    /// </summary>
    [Fact]
    public async Task AccessTokenProvider_ReturnsTheJobKeyAsIs()
    {
        // Arrange: HMAC(masterKey, jobName) — what DispatchLifecycleService stores in the per-job Secret.
        const string masterKey = "master-secret";
        const string jobName = "caa-aabbccdd";
        var jobKey = AgentKeyDerivation.DeriveAgentKey(masterKey, jobName);
        var doubleDerivation = AgentKeyDerivation.DeriveAgentKey(jobKey, jobName);

        // Sanity: double-derivation differs from single-derivation — this anchors the assertion below.
        doubleDerivation.Should().NotBe(jobKey,
            "HMAC(HMAC(master,job),job) must differ from HMAC(master,job) — otherwise this test cannot distinguish the two");

        var logger = Mock.Of<Serilog.ILogger>();
        await using var manager = new HubConnectionManager(
            orchestratorUrl: "http://localhost",
            agentId: new AgentId(jobName),
            apiKey: jobKey,
            logger: logger);

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

        // Act
        var actualToken = await opts.AccessTokenProvider!();

        // Assert
        actualToken.Should().Be(jobKey, "HubConnectionManager must use the Job's key directly as the bearer token");
        actualToken.Should().NotBe(doubleDerivation, "HubConnectionManager must not derive the Job's key again");
    }

    /// <summary>
    /// Verifies the security invariant: a pod holding only its own key HMAC(master, podA) cannot
    /// forge the credential for any other pod, because computing HMAC(master, podB) requires the
    /// master key, which is never distributed to pods.
    /// </summary>
    [Fact]
    public void JobKey_CannotBeUsedToImpersonateDifferentPod()
    {
        const string masterKey = "master-secret";
        const string podA = "caa-aabbccdd";
        const string podB = "caa-eeffgghh";

        var podAKey = AgentKeyDerivation.DeriveAgentKey(masterKey, podA);
        var expectedForPodB = AgentKeyDerivation.DeriveAgentKey(masterKey, podB);

        podAKey.Should().NotBe(expectedForPodB,
            "HMAC(master, podA) ≠ HMAC(master, podB) — per-pod isolation holds");
    }
}
