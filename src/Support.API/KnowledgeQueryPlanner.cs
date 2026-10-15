using System.Text.RegularExpressions;

namespace eShop.SupportApi;

public static class KnowledgeQueryPlanner
{
    public const int MaxQueries = 3;
    public const int MaxQueryLength = 2000;

    public static IReadOnlyList<string> Build(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQueryLength)
            throw new ArgumentException(
                $"The search query must contain 1 to {MaxQueryLength} characters.", nameof(query));

        // Keep the complete query so context and history references are not lost.
        var queries = new List<string> { query.Trim() };
        var questions = Regex.Matches(query, @"[^?？]+[?？]")
            .Select(match => match.Value.Trim())
            .Where(part => part.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
}
