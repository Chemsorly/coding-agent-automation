using System.Text.RegularExpressions;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Masks known secret values in triage text before it is sanitized, cut or posted. <see cref="SecretMasker"/>
/// matches exact values, so masking has to run on the raw text: sanitizing changes characters such as <c>@</c>
/// and <c>|</c>, and cutting can leave part of a secret.
/// </summary>
public static partial class TriageSecretMasking
{
    /// <summary>
    /// The credentials an MCP server configuration carries: every header value (and the token after a
    /// <c>Bearer</c>/<c>Basic</c>/<c>Token</c> scheme), and the values of environment variables whose name looks
    /// like a secret. Other environment values (URLs, log levels) stay readable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> McpSecrets(IEnumerable<McpServerConfig>? servers)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in servers ?? [])
        {
            foreach (var (name, value) in server.Headers)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                secrets[$"mcp:{server.Name}:header:{name}"] = value;
                var scheme = AuthSchemeRegex().Match(value);
                if (scheme.Success)
                    secrets[$"mcp:{server.Name}:header:{name}:token"] = scheme.Groups[1].Value;
            }
            foreach (var (name, value) in server.Env)
            {
                if (!string.IsNullOrWhiteSpace(value) && SecretNameRegex().IsMatch(name))
                    secrets[$"mcp:{server.Name}:env:{name}"] = value;
            }
        }
        return secrets;
    }

    /// <summary>A copy of <paramref name="result"/> with every known secret value in its text replaced.</summary>
    public static TriageResult Mask(TriageResult result, IReadOnlyCollection<KeyValuePair<string, string>> secrets)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(secrets);
        if (secrets.Count == 0)
            return result;

        string M(string value) => SecretMasker.Mask(value, secrets);
        string? N(string? value) => value is null ? null : M(value);

        return result with
        {
            Summary = M(result.Summary),
            Impact = N(result.Impact),
            Reproduction = N(result.Reproduction),
            DuplicateOf = N(result.DuplicateOf),
            CausalChain = result.CausalChain.Select(s => s with { Text = M(s.Text) }).ToList(),
            Hypotheses = result.Hypotheses.Select(h => h with { Text = M(h.Text) }).ToList(),
            Evidence = result.Evidence.Select(e => e with { Claim = M(e.Claim), Source = M(e.Source), Query = N(e.Query), Link = N(e.Link) }).ToList(),
            Investigated = result.Investigated.Select(c => c with { Check = M(c.Check), Where = M(c.Where), For = N(c.For), Result = M(c.Result) }).ToList(),
            NotChecked = result.NotChecked.Select(g => g with { What = M(g.What), Why = M(g.Why) }).ToList(),
            Questions = result.Questions.Select(q => q with { Question = M(q.Question), Why = N(q.Why) }).ToList(),
            Drafts = result.Drafts.Select(d => d with { Title = M(d.Title), Body = M(d.Body), Size = N(d.Size), Warning = N(d.Warning) }).ToList(),
        };
    }

    [GeneratedRegex(@"^\s*(?:Bearer|Basic|Token)\s+(\S{4,})\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AuthSchemeRegex();

    [GeneratedRegex("TOKEN|SECRET|PASSWORD|PASSWD|PWD|API_?KEY|ACCESS_?KEY|PRIVATE_?KEY|AUTH|CREDENTIAL|COOKIE|SESSION",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretNameRegex();
}
