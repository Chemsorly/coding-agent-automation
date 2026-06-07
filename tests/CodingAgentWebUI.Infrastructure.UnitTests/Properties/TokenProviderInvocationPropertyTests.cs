using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Property-based tests for token provider invocation on API calls.
/// Feature: github-app-auth, Property 10: Token provider invocation on API calls
/// </summary>
public class TokenProviderInvocationPropertyTests
{
    /// <summary>
    /// A fake API URL used when constructing providers.
    /// The actual HTTP call is never reached because the tracking delegate
    /// throws a sentinel exception to short-circuit the flow.
    /// </summary>
    private const string FakeApiUrl = "http://127.0.0.1:1";

    /// <summary>
    /// Sentinel exception thrown by the tracking delegate to prove it was invoked.
    /// Using a custom exception type ensures we can distinguish delegate invocation
    /// from other failures (network errors, null references, etc.).
    /// </summary>
    private sealed class TokenProviderInvokedException : Exception
    {
        public TokenProviderInvokedException() : base("Token provider was invoked") { }
    }

    /// <summary>
    /// Feature: github-app-auth, Property 10: Token provider invocation on API calls
    ///
    /// For any API method call on GitHubIssueProvider (GetIssueAsync, ListOpenIssuesAsync)
    /// or GitHubRepositoryProvider (CreatePullRequestAsync), the token provider delegate
    /// SHALL be invoked at least once to obtain a current token before the GitHub API call
    /// is made.
    ///
    /// Strategy: The tracking delegate throws a sentinel exception (TokenProviderInvokedException)
    /// when called. If the API method propagates this specific exception, it proves the delegate
    /// was invoked as part of the method's execution flow — before any HTTP call could be made.
    ///
    /// **Validates: Requirements 5.2, 5.3, 5.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(TokenProviderInvocationArbitrary)])]
    public void TokenProvider_IsInvokedBeforeApiCall(TokenProviderInvocationInput input)
    {
        // Arrange: Create a tracking delegate that throws a sentinel exception.
        // This proves the delegate was called and short-circuits the flow
        // before any real HTTP call is attempted (keeping the test fast).
        var invocationCount = 0;
        Func<CancellationToken, Task<string>> trackingDelegate = _ =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new TokenProviderInvokedException();
        };

        // Act: Call the selected API method — expect the sentinel exception
        Exception? caughtException = null;

        try
        {
            switch (input.Method)
            {
                case ApiMethodChoice.IssueProvider_GetIssue:
                {
                    var provider = new GitHubIssueProvider(
                        new GitHubConnectionInfo(FakeApiUrl, input.Owner, input.Repo), trackingDelegate);
                    provider.GetIssueAsync(
                        input.IssueNumber.ToString(), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;
                }
                case ApiMethodChoice.IssueProvider_ListOpenIssues:
                {
                    var provider = new GitHubIssueProvider(
                        new GitHubConnectionInfo(FakeApiUrl, input.Owner, input.Repo), trackingDelegate);
                    provider.ListOpenIssuesAsync(1, 25, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;
                }
                case ApiMethodChoice.RepoProvider_CreatePullRequest:
                {
                    var provider = new GitHubRepositoryProvider(
                        new GitHubConnectionInfo(FakeApiUrl, input.Owner, input.Repo), trackingDelegate, input.BaseBranch);
                    var prInfo = new PullRequestInfo
                    {
                        Title = input.PrTitle,
                        Body = input.PrBody,
                        BranchName = input.BranchName,
                        BaseBranch = input.BaseBranch,
                        IsDraft = false
                    };
                    provider.CreatePullRequestAsync(prInfo, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }

        // Assert: The token provider delegate was invoked at least once
        Assert.True(
            invocationCount >= 1,
            $"Token provider delegate was not invoked for {input.Method}. " +
            $"Expected at least 1 invocation, got {invocationCount}.");

        // Assert: The caught exception is our sentinel, proving the delegate
        // was called as part of the API method's execution flow.
        // The exception may be wrapped in an AggregateException by async machinery.
        Assert.NotNull(caughtException);
        var innermost = GetInnermostException(caughtException);
        Assert.IsType<TokenProviderInvokedException>(innermost);
    }

    /// <summary>
    /// Unwraps nested AggregateExceptions to find the innermost cause.
    /// Async methods may wrap exceptions in AggregateException layers.
    /// </summary>
    private static Exception GetInnermostException(Exception ex)
    {
        while (ex is AggregateException agg && agg.InnerException is not null)
            ex = agg.InnerException;

        return ex;
    }
}

/// <summary>
/// Enum representing which API method to test for token provider invocation.
/// </summary>
public enum ApiMethodChoice
{
    IssueProvider_GetIssue,
    IssueProvider_ListOpenIssues,
    RepoProvider_CreatePullRequest
}

/// <summary>
/// Input type for token provider invocation property tests.
/// Contains the method to call and all parameters needed for each method variant.
/// </summary>
public record TokenProviderInvocationInput(
    string Owner,
    string Repo,
    string BaseBranch,
    ApiMethodChoice Method,
    int IssueNumber,
    string PrTitle,
    string PrBody,
    string BranchName)
{
    public override string ToString() =>
        $"Method={Method}, Owner={Owner}, Repo={Repo}, IssueNumber={IssueNumber}";
}

/// <summary>
/// FsCheck Arbitrary that generates token provider invocation test inputs.
/// Generates random owner/repo names, issue numbers, PR details, and randomly
/// selects which API method to test.
/// </summary>
public static class TokenProviderInvocationArbitrary
{
    public static Arbitrary<TokenProviderInvocationInput> TokenProviderInvocationInput()
    {
        // Generate owner/repo names: lowercase alphanumeric, 3-20 chars
        var nameChars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
        var nameGen =
            from len in Gen.Choose(3, 20)
            from chars in Gen.Elements(nameChars).ArrayOf(len)
            select new string(chars);

        // Generate branch names: lowercase alphanumeric with hyphens, 3-30 chars
        var branchChars = "abcdefghijklmnopqrstuvwxyz0123456789-".ToCharArray();
        var branchGen =
            from len in Gen.Choose(3, 30)
            from chars in Gen.Elements(branchChars).ArrayOf(len)
            // Ensure branch name starts and ends with alphanumeric
            let raw = new string(chars)
            select raw.Trim('-').Length > 0 ? raw.Trim('-') : "main";

        // Generate issue numbers: positive integers
        var issueNumberGen = Gen.Choose(1, 99999);

        // Generate PR titles and bodies: simple alphanumeric strings
        var textChars = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();
        var textGen =
            from len in Gen.Choose(5, 50)
            from chars in Gen.Elements(textChars).ArrayOf(len)
            select new string(chars);

        // Randomly select which API method to test
        var methodGen = Gen.Elements(
            ApiMethodChoice.IssueProvider_GetIssue,
            ApiMethodChoice.IssueProvider_ListOpenIssues,
            ApiMethodChoice.RepoProvider_CreatePullRequest);

        var combined =
            from owner in nameGen
            from repo in nameGen
            from baseBranch in branchGen
            from method in methodGen
            from issueNumber in issueNumberGen
            from prTitle in textGen
            from prBody in textGen
            from branchName in branchGen
            select new TokenProviderInvocationInput(
                owner, repo, baseBranch, method, issueNumber, prTitle, prBody, branchName);

        return combined.ToArbitrary();
    }
}
