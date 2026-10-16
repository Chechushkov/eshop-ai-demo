using System.Text.RegularExpressions;

namespace eShop.SupportApi;

public static class KnowledgeQueryPlanner
{
    public const int MaxQueries = 3;
    public const int MaxQueryLength = 2000;

    public static IReadOnlyList<string> Build(string query, string? originalQuestion = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength)
            throw new ArgumentException(
                $"The search query must contain 1 to {MaxQueryLength} characters.", nameof(query));

        if (originalQuestion is not null &&
            (string.IsNullOrWhiteSpace(originalQuestion) || originalQuestion.Length > MaxQueryLength))
            throw new ArgumentException(
                $"The original question must contain 1 to {MaxQueryLength} characters.", nameof(originalQuestion));

        // Keep the resolved query for history references. A rewrite or an agent's
        // short tool argument must not erase separate parts of the buyer's question.
        var queries = new List<string> { query.Trim() };
        var originalParts = originalQuestion is null ? [] : Questions(originalQuestion);
        var questions = originalParts.Length >= 2 ? originalParts : Questions(query);

        // This syntactic split handles separate questions, not arbitrary topic detection.
        // Single-question requests keep the existing one-search cost.
        if (questions.Length < 2)
            return queries;

        foreach (string question in questions)
        {
            if (queries.Count == MaxQueries)
                break;

            if (!queries.Contains(question, StringComparer.OrdinalIgnoreCase))
                queries.Add(question);
        }

        return queries;
    }

    private static string[] Questions(string text) => Regex.Matches(text, @"[^?？]+[?？]")
        .Select(match => match.Value.Trim())
        .Where(part => part.Any(char.IsLetterOrDigit))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
