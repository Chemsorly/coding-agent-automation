using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure;
using Xunit;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Property-based tests for ProviderFactory config validation.
/// Feature: github-app-auth, provider-interface-gaps
/// </summary>
public class ProviderFactoryPropertyTests
{
    private static readonly PipelineConfiguration DefaultPipelineConfig = new();

    /// <summary>
    /// Generates a valid base64-encoded RSA private key PEM string.
    /// </summary>
    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    }

    /// <summary>
    /// Feature: github-app-auth, Property 1: Config key correctness
    ///
    /// For any valid set of GitHub App credentials (clientId, installationId,
    /// privateKeyBase64), a GitHub issue provider ProviderConfig SHALL have a Settings
    /// dictionary containing exactly the keys: clientId, installationId,
    /// privateKeyBase64, apiUrl, owner, repo — and SHALL NOT contain a token key.
    /// For repository providers, the Settings dictionary SHALL additionally contain baseBranch.
    ///
    /// Strategy: Generate random valid credential values, build ProviderConfig objects
    /// with the expected keys, verify the Settings dictionary structure, and verify
    /// the ProviderFactory accepts the config without validation errors.
    ///
    /// **Validates: Requirements 2.1, 2.4**
    /// </summary>
    [Property(Arbitrary = [typeof(ConfigKeyCorrectnessArbitrary)])]
    public void ConfigKeys_IssueProvider_ContainsCorrectKeysAndNoToken(ConfigKeyCorrectnessInput input)
    {
        // Arrange: Build a ProviderConfig with the expected GitHub App keys for an issue provider
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = input.DisplayName,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
                [ProviderSettingKeys.ClientId] = input.ClientId,
                [ProviderSettingKeys.InstallationId] = input.InstallationId.ToString(),
                [ProviderSettingKeys.PrivateKeyBase64] = input.PrivateKeyBase64,
                [ProviderSettingKeys.Owner] = input.Owner,
                [ProviderSettingKeys.Repo] = input.Repo
            }
        };

        // Assert: Settings contains all required keys
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.ClientId), "Settings must contain 'clientId'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.InstallationId), "Settings must contain 'installationId'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64), "Settings must contain 'privateKeyBase64'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.ApiUrl), "Settings must contain 'apiUrl'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.Owner), "Settings must contain 'owner'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.Repo), "Settings must contain 'repo'");

        // Assert: Settings does NOT contain a 'token' key (PAT has been replaced)
        Assert.False(config.Settings.ContainsKey(ProviderSettingKeys.Token), "Settings must NOT contain 'token' — PAT has been replaced by GitHub App auth");

        // Assert: The ProviderFactory accepts this config without throwing a validation error.
        // CreateIssueProvider will call ValidateRequiredSettings internally.
        // It will also create a GitHubAppAuthService which validates the private key.
        var factory = new ProviderFactory(DefaultPipelineConfig);
        var exception = Record.Exception(() => factory.CreateIssueProvider(config));
        Assert.Null(exception);
    }

    /// <summary>
    /// Feature: github-app-auth, Property 1: Config key correctness
    ///
    /// Same property as above but for repository providers, which additionally require baseBranch.
    ///
    /// **Validates: Requirements 2.1, 2.4**
    /// </summary>
    [Property(Arbitrary = [typeof(ConfigKeyCorrectnessArbitrary)])]
    public void ConfigKeys_RepoProvider_ContainsCorrectKeysAndNoToken(ConfigKeyCorrectnessInput input)
    {
        // Arrange: Build a ProviderConfig with the expected GitHub App keys for a repo provider
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = input.DisplayName,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = input.ApiUrl,
                [ProviderSettingKeys.ClientId] = input.ClientId,
                [ProviderSettingKeys.InstallationId] = input.InstallationId.ToString(),
                [ProviderSettingKeys.PrivateKeyBase64] = input.PrivateKeyBase64,
                [ProviderSettingKeys.Owner] = input.Owner,
                [ProviderSettingKeys.Repo] = input.Repo,
                [ProviderSettingKeys.BaseBranch] = input.BaseBranch
            }
        };

        // Assert: Settings contains all required keys including baseBranch
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.ClientId), "Settings must contain 'clientId'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.InstallationId), "Settings must contain 'installationId'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64), "Settings must contain 'privateKeyBase64'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.ApiUrl), "Settings must contain 'apiUrl'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.Owner), "Settings must contain 'owner'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.Repo), "Settings must contain 'repo'");
        Assert.True(config.Settings.ContainsKey(ProviderSettingKeys.BaseBranch), "Settings must contain 'baseBranch'");

        // Assert: Settings does NOT contain a 'token' key (PAT has been replaced)
        Assert.False(config.Settings.ContainsKey(ProviderSettingKeys.Token), "Settings must NOT contain 'token' — PAT has been replaced by GitHub App auth");

        // Assert: The ProviderFactory accepts this config without throwing a validation error.
        var factory = new ProviderFactory(DefaultPipelineConfig);
        var exception = Record.Exception(() => factory.CreateRepositoryProvider(config));
        Assert.Null(exception);
    }

    /// <summary>
    /// Feature: provider-interface-gaps, Property 7: Auth Service Cache Consistency
    ///
    /// For any set of ProviderConfig objects, the ProviderFactory returns the same
    /// GitHubAppAuthService instance (reference equality) for configs sharing the same
    /// clientId + installationId composite key, and distinct instances for configs with
    /// different composite keys.
    ///
    /// Strategy: Generate a list of ProviderConfig objects with controlled clientId/installationId
    /// overlap. Call GetOrCreateAuthService for each config on the same factory instance.
    /// Group results by composite key and verify reference equality within groups and
    /// reference inequality across groups.
    ///
    /// **Validates: Requirements 5.1**
    /// </summary>
    [Property(Arbitrary = [typeof(AuthCacheConsistencyArbitrary)])]
    public void AuthServiceCache_SameKey_ReturnsSameInstance_DifferentKey_ReturnsDistinct(AuthCacheConsistencyInput input)
    {
        // Arrange
        var factory = new ProviderFactory(DefaultPipelineConfig);

        // Act: Call GetOrCreateAuthService for each config and collect results
        var results = input.Configs
            .Select(config => (Config: config, AuthService: factory.GetOrCreateAuthService(config)))
            .ToList();

        // Group by composite key (clientId:installationId)
        var groupedByKey = results
            .GroupBy(r => $"{r.Config.Settings[ProviderSettingKeys.ClientId]}:{r.Config.Settings[ProviderSettingKeys.InstallationId]}")
            .ToList();

        // Assert: Within each group, all instances are the same reference
        foreach (var group in groupedByKey)
        {
            var instances = group.Select(g => g.AuthService).ToList();
            var first = instances[0];
            foreach (var instance in instances.Skip(1))
            {
                Assert.Same(first, instance);
            }
        }

        // Assert: Across different groups, instances are distinct references
        var distinctKeys = groupedByKey.Select(g => g.First().AuthService).ToList();
        Assert.Equal(distinctKeys.Count, distinctKeys.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    /// <summary>
    /// Feature: github-app-auth, Property 2: Missing config field validation
    ///
    /// For any ProviderConfig where at least one of clientId, installationId, or
    /// privateKeyBase64 is missing from the Settings dictionary or has an empty/whitespace
    /// value, the ProviderFactory SHALL throw an ArgumentException whose message identifies
    /// the missing setting(s).
    ///
    /// Strategy: Generate configs with random combinations of missing/empty fields among
    /// the three critical auth fields. Verify the factory throws ArgumentException and
    /// the message contains each missing field name.
    ///
    /// **Validates: Requirements 2.1, 2.4**
    /// </summary>
    [Property(Arbitrary = [typeof(MissingConfigFieldArbitrary)])]
    public void MissingConfigField_ThrowsArgumentExceptionIdentifyingMissingSettings(MissingConfigFieldInput input)
    {
        // Arrange: Build a ProviderConfig with some fields missing or empty
        var settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "testowner",
            [ProviderSettingKeys.Repo] = "testrepo"
        };

        // Add or omit each field based on the input
        if (input.ClientIdPresence == FieldPresence.Present)
            settings[ProviderSettingKeys.ClientId] = input.ClientIdValue;
        else if (input.ClientIdPresence == FieldPresence.Empty)
            settings[ProviderSettingKeys.ClientId] = input.ClientIdValue; // empty or whitespace

        if (input.InstallationIdPresence == FieldPresence.Present)
            settings[ProviderSettingKeys.InstallationId] = input.InstallationIdValue;
        else if (input.InstallationIdPresence == FieldPresence.Empty)
            settings[ProviderSettingKeys.InstallationId] = input.InstallationIdValue; // empty or whitespace

        if (input.PrivateKeyPresence == FieldPresence.Present)
            settings[ProviderSettingKeys.PrivateKeyBase64] = input.PrivateKeyBase64Value;
        else if (input.PrivateKeyPresence == FieldPresence.Empty)
            settings[ProviderSettingKeys.PrivateKeyBase64] = input.PrivateKeyBase64Value; // empty or whitespace

        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Provider",
            Settings = settings
        };

        // Act & Assert: The factory must throw ArgumentException
        var factory = new ProviderFactory(DefaultPipelineConfig);
        var ex = Assert.Throws<ArgumentException>(() => factory.CreateIssueProvider(config));

        // Assert: The exception message identifies each missing setting
        foreach (var missingField in input.ExpectedMissingFields)
        {
            Assert.Contains(missingField, ex.Message,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// Enum representing whether a config field is present with a valid value,
/// present but empty/whitespace, or completely missing from the dictionary.
/// </summary>
public enum FieldPresence
{
    Present,
    Empty,
    Missing
}

/// <summary>
/// Input type for config key correctness property tests.
/// Contains all the credential values needed to build a valid ProviderConfig.
/// </summary>
public record ConfigKeyCorrectnessInput(
    string DisplayName,
    string ApiUrl,
    string ClientId,
    long InstallationId,
    string PrivateKeyBase64,
    string Owner,
    string Repo,
    string BaseBranch)
{
    public override string ToString() =>
        $"ClientId={ClientId}, InstallationId={InstallationId}, Owner={Owner}, Repo={Repo}";
}

/// <summary>
/// Input type for missing config field property tests.
/// Specifies which fields are present, empty, or missing, and the expected missing field names.
/// </summary>
public record MissingConfigFieldInput(
    FieldPresence ClientIdPresence,
    string ClientIdValue,
    FieldPresence InstallationIdPresence,
    string InstallationIdValue,
    FieldPresence PrivateKeyPresence,
    string PrivateKeyBase64Value,
    string[] ExpectedMissingFields)
{
    public override string ToString() =>
        $"ClientId={ClientIdPresence}, InstallationId={InstallationIdPresence}, PrivateKey={PrivateKeyPresence}, Missing=[{string.Join(", ", ExpectedMissingFields)}]";
}

/// <summary>
/// FsCheck Arbitrary that generates valid GitHub App credential inputs for config key correctness tests.
/// Generates random but valid credential values including a real RSA private key.
/// </summary>
public static class ConfigKeyCorrectnessArbitrary
{
    /// <summary>
    /// Pre-generated valid base64-encoded RSA private key.
    /// RSA key generation is expensive, so we reuse a single key across all test iterations.
    /// The property under test is about config structure, not key validity.
    /// </summary>
    private static readonly string ValidPrivateKeyBase64 = GenerateSharedPrivateKeyBase64();

    private static string GenerateSharedPrivateKeyBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    }

    public static Arbitrary<ConfigKeyCorrectnessInput> ConfigKeyCorrectnessInput()
    {
        // Generate Client IDs: Iv1. prefix + alphanumeric, matching GitHub's format
        var alphanumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
        var clientIdGen =
            from len in Gen.Choose(8, 16)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select "Iv1." + new string(chars);

        // Generate Installation IDs: positive longs
        var installationIdGen =
            from id in Gen.Choose(1, 999999)
            select (long)id;

        // Generate owner/repo names: lowercase alphanumeric, 3-20 chars
        var nameGen =
            from len in Gen.Choose(3, 20)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select new string(chars);

        // Generate display names
        var displayNameGen =
            from name in nameGen
            select $"GitHub-{name}";

        // Generate branch names
        var branchChars = "abcdefghijklmnopqrstuvwxyz0123456789-".ToCharArray();
        var branchGen =
            from len in Gen.Choose(3, 20)
            from chars in Gen.Elements(branchChars).ArrayOf(len)
            let raw = new string(chars)
            select raw.Trim('-').Length > 0 ? raw.Trim('-') : "main";

        var combined =
            from displayName in displayNameGen
            from clientId in clientIdGen
            from installationId in installationIdGen
            from owner in nameGen
            from repo in nameGen
            from baseBranch in branchGen
            select new ConfigKeyCorrectnessInput(
                displayName,
                "https://api.github.com",
                clientId,
                installationId,
                ValidPrivateKeyBase64,
                owner,
                repo,
                baseBranch);

        return combined.ToArbitrary();
    }
}

/// <summary>
/// FsCheck Arbitrary that generates missing config field inputs.
/// Ensures at least one of clientId, installationId, or privateKeyBase64 is missing or empty.
/// </summary>
public static class MissingConfigFieldArbitrary
{
    public static Arbitrary<MissingConfigFieldInput> MissingConfigFieldInput()
    {
        // Generate a valid private key base64 for the "present" case
        // (reuse a single key since key validity isn't the focus here)
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var validKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));

        // Generate field presence: Present, Empty, or Missing
        var presenceGen = Gen.Elements(FieldPresence.Present, FieldPresence.Empty, FieldPresence.Missing);

        // Generate empty/whitespace values for the "Empty" case
        var emptyValueGen = Gen.Elements("", " ", "  ", "\t", " \t ");

        // Generate valid client IDs for the "Present" case
        var alphanumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
        var validClientIdGen =
            from len in Gen.Choose(8, 16)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select "Iv1." + new string(chars);

        // Generate valid installation IDs for the "Present" case
        var validInstallationIdGen =
            from id in Gen.Choose(1, 999999)
            select id.ToString();

        var combined =
            from clientIdPresence in presenceGen
            from installationIdPresence in presenceGen
            from privateKeyPresence in presenceGen
            // Ensure at least one field is NOT present (missing or empty)
            where clientIdPresence != FieldPresence.Present
                  || installationIdPresence != FieldPresence.Present
                  || privateKeyPresence != FieldPresence.Present
            from clientIdValue in clientIdPresence == FieldPresence.Present
                ? validClientIdGen
                : clientIdPresence == FieldPresence.Empty ? emptyValueGen : Gen.Constant("")
            from installationIdValue in installationIdPresence == FieldPresence.Present
                ? validInstallationIdGen
                : installationIdPresence == FieldPresence.Empty ? emptyValueGen : Gen.Constant("")
            from privateKeyValue in privateKeyPresence == FieldPresence.Present
                ? Gen.Constant(validKeyBase64)
                : privateKeyPresence == FieldPresence.Empty ? emptyValueGen : Gen.Constant("")
            let expectedMissing = new List<string>()
            select new MissingConfigFieldInput(
                clientIdPresence,
                clientIdValue,
                installationIdPresence,
                installationIdValue,
                privateKeyPresence,
                privateKeyValue,
                BuildExpectedMissingFields(clientIdPresence, installationIdPresence, privateKeyPresence));

        return combined.ToArbitrary();
    }

    private static string[] BuildExpectedMissingFields(
        FieldPresence clientId, FieldPresence installationId, FieldPresence privateKey)
    {
        var missing = new List<string>();
        if (clientId != FieldPresence.Present) missing.Add(ProviderSettingKeys.ClientId);
        if (installationId != FieldPresence.Present) missing.Add(ProviderSettingKeys.InstallationId);
        if (privateKey != FieldPresence.Present) missing.Add(ProviderSettingKeys.PrivateKeyBase64);
        return missing.ToArray();
    }
}

/// <summary>
/// Input type for auth service cache consistency property tests.
/// Contains a list of ProviderConfig objects with controlled clientId/installationId overlap.
/// </summary>
public record AuthCacheConsistencyInput(List<ProviderConfig> Configs, int ExpectedDistinctKeys)
{
    public override string ToString() =>
        $"Configs={Configs.Count}, ExpectedDistinctKeys={ExpectedDistinctKeys}";
}

/// <summary>
/// FsCheck Arbitrary that generates sets of ProviderConfig objects with controlled
/// clientId/installationId overlap for testing auth service cache consistency.
/// Generates 2-4 distinct composite keys and 3-8 configs that reference them,
/// ensuring some configs share the same key.
/// </summary>
public static class AuthCacheConsistencyArbitrary
{
    /// <summary>
    /// Pre-generated valid base64-encoded RSA private key.
    /// RSA key generation is expensive, so we reuse a single key across all test iterations.
    /// The property under test is about caching behavior, not key validity.
    /// </summary>
    private static readonly string ValidPrivateKeyBase64 = GenerateSharedPrivateKeyBase64();

    private static string GenerateSharedPrivateKeyBase64()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    }

    public static Arbitrary<AuthCacheConsistencyInput> AuthCacheConsistencyInput()
    {
        var alphanumChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

        // Generate client IDs: Iv1. prefix + alphanumeric
        var clientIdGen =
            from len in Gen.Choose(8, 16)
            from chars in Gen.Elements(alphanumChars).ArrayOf(len)
            select "Iv1." + new string(chars);

        // Generate installation IDs: positive integers
        var installationIdGen =
            from id in Gen.Choose(1, 999999)
            select id;

        // Generate a pool of 2-4 distinct (clientId, installationId) pairs
        var keyPoolGen =
            from keyCount in Gen.Choose(2, 4)
            from keys in Gen.Zip(clientIdGen, installationIdGen).ArrayOf(keyCount)
            // Ensure keys are actually distinct by composite key string
            let distinctKeys = keys
                .GroupBy(k => $"{k.Item1}:{k.Item2}")
                .Select(g => g.First())
                .ToArray()
            where distinctKeys.Length >= 2
            select distinctKeys;

        // Generate 3-8 configs, each picking a key from the pool (ensuring overlap)
        var combined =
            from keyPool in keyPoolGen
            from configCount in Gen.Choose(3, 8)
            from keyIndices in Gen.Choose(0, keyPool.Length - 1).ArrayOf(configCount)
            let configs = keyIndices.Select((keyIdx, i) =>
            {
                var (clientId, installationId) = keyPool[keyIdx];
                return new ProviderConfig
                {
                    Kind = ProviderKind.Issue,
                    ProviderType = "GitHub",
                    DisplayName = $"TestProvider-{i}",
                    Settings = new Dictionary<string, string>
                    {
                        [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                        [ProviderSettingKeys.ClientId] = clientId,
                        [ProviderSettingKeys.InstallationId] = installationId.ToString(),
                        [ProviderSettingKeys.PrivateKeyBase64] = ValidPrivateKeyBase64,
                        [ProviderSettingKeys.Owner] = "testowner",
                        [ProviderSettingKeys.Repo] = "testrepo"
                    }
                };
            }).ToList()
            let expectedDistinct = configs
                .Select(c => $"{c.Settings[ProviderSettingKeys.ClientId]}:{c.Settings[ProviderSettingKeys.InstallationId]}")
                .Distinct()
                .Count()
            select new AuthCacheConsistencyInput(configs, expectedDistinct);

        return combined.ToArbitrary();
    }
}

