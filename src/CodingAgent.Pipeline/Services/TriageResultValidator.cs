using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Deterministic checks on a parsed triage result, after the adversarial review: rules that must hold whatever
/// the agents wrote, and size limits that keep the result within the hub message limit and the tracker comment
/// limit.
/// </summary>
public static class TriageResultValidator
{
    /// <summary>The validated (and possibly trimmed) result, or why the run must fail, and what was changed.</summary>
    public sealed record ValidationOutcome(TriageResult? Result, string? Error, IReadOnlyList<string> Warnings);

    internal const int MaxSummaryChars = 4_000;
    internal const int MaxFieldChars = 2_000;
    internal const int MaxTitleChars = 200;
    internal const int MaxListItems = 60;
    internal const int MaxShortListItems = 20;
    internal const int MaxQuestions = 10;

    internal const string GenericQuestion =
        "What else do you know about when and where the problem happens (time, environment, affected users, recent changes)?";

    /// <param name="result">The parsed result.</param>
    /// <param name="repositoryNames">The project's repository (template) names a draft may target; empty when unknown.</param>
    /// <param name="executorRepository">The run's own repository name: the target of a draft whose repository is unknown.</param>
    public static ValidationOutcome Validate(
        TriageResult result, IReadOnlyCollection<string> repositoryNames, string? executorRepository)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(repositoryNames);

        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(result.Summary))
            return new ValidationOutcome(null, "The result has no summary.", warnings);

        if (result.Verdict == TriageVerdict.CauseFound && result.Investigated.Count == 0)
            return new ValidationOutcome(null,
                "The result claims a root cause but lists nothing it investigated.", warnings);

        var drafts = ValidateDrafts(result.Drafts, repositoryNames, executorRepository, warnings);

        var questions = result.Questions.Take(MaxQuestions).Select(q => q with
        {
            Question = Cut(q.Question, MaxFieldChars),
            Why = CutOrNull(q.Why, MaxFieldChars),
        }).ToList();
        if (result.Verdict == TriageVerdict.Inconclusive && questions.Count == 0)
        {
            questions.Add(new TriageQuestion { Question = GenericQuestion });
            warnings.Add("An inconclusive result asked no questions; a generic question was added.");
        }

        var validated = result with
        {
            Summary = Cut(result.Summary, MaxSummaryChars),
            Impact = CutOrNull(result.Impact, MaxFieldChars),
            Reproduction = CutOrNull(result.Reproduction, MaxFieldChars),
            DuplicateOf = CutOrNull(result.DuplicateOf, MaxFieldChars),
            CausalChain = Capped(result.CausalChain, MaxShortListItems, "causal chain links", warnings)
                .Select(c => c with { Text = Cut(c.Text, MaxFieldChars) }).ToList(),
            Hypotheses = Capped(result.Hypotheses, MaxShortListItems, "hypotheses", warnings)
                .Select(h => h with { Text = Cut(h.Text, MaxFieldChars) }).ToList(),
            Evidence = Capped(result.Evidence, MaxListItems, "evidence entries", warnings)
                .Select(e => e with
                {
                    Claim = Cut(e.Claim, MaxFieldChars),
                    Source = Cut(e.Source, MaxTitleChars),
                    Query = CutOrNull(e.Query, MaxFieldChars),
                    Link = CutOrNull(e.Link, MaxFieldChars),
                }).ToList(),
            Investigated = Capped(result.Investigated, MaxListItems, "investigated checks", warnings)
                .Select(c => c with
                {
                    Check = Cut(c.Check, MaxFieldChars),
                    Where = Cut(c.Where, MaxTitleChars),
                    For = CutOrNull(c.For, MaxTitleChars),
                    Result = Cut(c.Result, MaxFieldChars),
                }).ToList(),
            NotChecked = Capped(result.NotChecked, MaxShortListItems, "not-checked items", warnings)
                .Select(g => g with { What = Cut(g.What, MaxFieldChars), Why = Cut(g.Why, MaxFieldChars) }).ToList(),
            Questions = questions,
            Drafts = drafts,
        };

        return new ValidationOutcome(validated, null, warnings);
    }

    private static List<TriageDraft> ValidateDrafts(
        IReadOnlyList<TriageDraft> drafts, IReadOnlyCollection<string> repositoryNames, string? executorRepository,
        List<string> warnings)
    {
        var valid = new List<TriageDraft>();
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.Body))
            {
                warnings.Add($"Dropped draft '{draft.Id}': it has no title or no body.");
                continue;
            }

            if (valid.Count == TriageConstants.MaxDrafts)
            {
                warnings.Add($"Dropped drafts beyond the first {TriageConstants.MaxDrafts}.");
                break;
            }

            string? warning = null;
            var target = draft.TargetRepository.Trim();
            if (repositoryNames.Count > 0)
            {
                var match = repositoryNames.FirstOrDefault(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    target = match;
                }
                else if (!string.IsNullOrEmpty(executorRepository))
                {
                    warning = string.IsNullOrEmpty(target)
                        ? $"No repository given; set to {executorRepository}."
                        : $"Unknown repository '{target}'; set to {executorRepository}.";
                    warnings.Add($"Draft '{draft.Title}': {warning}");
                    target = executorRepository;
                }
            }

            var body = draft.Body;
            if (body.Length > TriageConstants.MaxDraftBodyChars)
            {
                body = body[..TriageConstants.MaxDraftBodyChars] + "\n\n…(cut)";
                warnings.Add($"Draft '{draft.Title}': the body was cut to {TriageConstants.MaxDraftBodyChars} characters.");
            }

            valid.Add(draft with
            {
                Id = $"d{valid.Count + 1}",
                TargetRepository = target,
                Title = Cut(draft.Title.Trim(), MaxTitleChars),
                Body = body,
                Size = CutOrNull(draft.Size, MaxTitleChars),
                Warning = warning,
            });
        }

        return valid;
    }

    private static IEnumerable<T> Capped<T>(IReadOnlyList<T> items, int max, string what, List<string> warnings)
    {
        if (items.Count > max)
            warnings.Add($"Kept the first {max} of {items.Count} {what}.");
        return items.Take(max);
    }

    private static string Cut(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string? CutOrNull(string? value, int max) =>
        value is null ? null : Cut(value, max);
}
