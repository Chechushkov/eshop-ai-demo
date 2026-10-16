using System.Globalization;
using System.Text.RegularExpressions;

namespace eShop.SupportApi;

// Application-owned notices are assembled after draft validation in both modes.
// Retrieved text and model output cannot register or change these rules.
public static class ReplyNotices
{
    private const string WarrantySourcePrefix = "support-warranty#";

    public const string WarrantyAuthority =
        "Помощник не может подтвердить дефект или одобрить замену либо возврат денег. " +
        "Решение принимает поддержка.";

    // Match only the fixed wording at the end, allowing whitespace wrapping and
    // optional citations. This is not a semantic classifier for paraphrases.
    private static readonly Regex TrailingWarrantyAuthority = new(
        Regex.Escape(WarrantyAuthority).Replace(@"\ ", @"\s+") +
        @"(?:\s*\[(?<sourceId>support-warranty#[0-9]+)\])*\s*\z",
        RegexOptions.CultureInvariant);

    public static string BuildInstructions(RunContext context) =>
        WarrantySource(context) is null
            ? ""
            : "\nC# appends this mandatory warranty notice to the final reply: " +
                WarrantyAuthority +
                " Explain the relevant procedure, but do not repeat the notice in your draft.";

    public static string Append(string text, RunContext context)
    {
        var source = WarrantySource(context);
        if (source is null)
            return text;

        // Use an actual selected chunk ID, not a fabricated #001 citation.
        string paragraph = $"{WarrantyAuthority} [{source.Id}]";
        string trimmed = text.TrimEnd();
        var allowed = context.Sources.Where(IsWarrantySource)
            .Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal);

        // A model may copy the fixed notice without its citation, or repeat it
        // from history. Replace trailing copies with one application-owned block.
        while (TryRemoveTrailingNotice(trimmed, allowed, out string remaining))
            trimmed = remaining;

        string result = string.IsNullOrWhiteSpace(trimmed)
            ? paragraph
            : trimmed + "\n\n" + paragraph;
        if (result == text)
            return text;

        context.Log("reply notice warranty_authority");
        return result;
    }

    private static bool TryRemoveTrailingNotice(string text, HashSet<string> allowed, out string remaining)
    {
        remaining = text;
        var match = TrailingWarrantyAuthority.Match(text);
        if (!match.Success ||
            match.Index > 0 && !char.IsWhiteSpace(text[match.Index - 1]))
            return false;

        // Never remove a notice carrying an unavailable source ID. Validation
        // must still see and reject that citation. Quoted occurrences stay intact.
        if (match.Groups["sourceId"].Captures.Any(capture => !allowed.Contains(capture.Value)))
            return false;

        string prefix = text[..match.Index].TrimEnd();
        bool startsLine = text.AsSpan(prefix.Length, match.Index - prefix.Length).Contains('\n');
        if (prefix.Length > 0 && !startsLine && !".!?]".Contains(prefix[^1]))
            return false;

        remaining = prefix;
        return true;
    }

    private static KnowledgeHit? WarrantySource(RunContext context) =>
        context.Sources.FirstOrDefault(IsWarrantySource);

    private static bool IsWarrantySource(KnowledgeHit hit) =>
        hit.Id.StartsWith(WarrantySourcePrefix, StringComparison.Ordinal) &&
        int.TryParse(hit.Id.AsSpan(WarrantySourcePrefix.Length),
            NumberStyles.None, CultureInfo.InvariantCulture, out int chunk) && chunk > 0;
}
