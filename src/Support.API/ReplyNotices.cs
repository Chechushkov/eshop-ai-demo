using System.Globalization;
using System.Text.RegularExpressions;

namespace eShop.SupportApi;

// Application-owned notices are assembled after draft validation in both modes.
// Retrieved text and model output cannot register or change these rules.
public static class ReplyNotices
{
    public const string WarrantyAuthority =
        "Помощник не может подтвердить дефект или одобрить замену либо возврат денег. " +
        "Решение принимает поддержка.";

    public const string ReturnPolicyScope =
        "Это правило учебного магазина, а не изложение закона о защите прав потребителей.";

    public const string TicketOutcome =
        "Создание заявки не устанавливает дату доставки и не одобряет компенсацию, отмену заказа или возврат денег. " +
        "Заявка сохранена в учебной системе и не отправлена во внешнюю службу.";

    private static readonly NoticeRule WarrantyRule =
        new("warranty_authority", WarrantyAuthority, "support-warranty#");
    private static readonly NoticeRule ReturnRule =
        new("return_policy_scope", ReturnPolicyScope, "support-returns-period#");
    private static readonly NoticeRule TicketRule =
        new("ticket_outcome", TicketOutcome, null);

    public static string BuildInstructions(RunContext context) =>
        string.Concat(ActiveNotices(context).Select(notice =>
            "\nC# appends this mandatory notice to the final reply: " + notice.Rule.Text +
            " Explain the relevant procedure, but do not repeat the notice in your draft."));

    public static string Append(string text, RunContext context)
    {
        var notices = ActiveNotices(context);
        string result = Format(text, notices);
        if (result == text)
            return text;

        foreach (var notice in notices)
            context.Log("reply notice " + notice.Rule.Name);
        return result;
    }

    // Validation can recognize the final confirmed-data reply without logging,
    // calling the model or modifying the request context.
    internal static string Format(string text, RunContext context) =>
        Format(text, ActiveNotices(context));

    private static string Format(string text, List<ActiveNotice> notices)
    {
        if (notices.Count == 0)
            return text;

        string trimmed = text.TrimEnd();
        while (true)
        {
            bool removed = false;
            foreach (var notice in notices)
            {
                if (!TryRemoveTrailingNotice(trimmed, notice, out string remaining, out var citations))
                    continue;

                // Tool-owned wording has no required knowledge citation. Keep
                // any valid citations on a copied block instead of erasing them.
                if (notice.Source is null)
                    foreach (string id in citations)
                        if (!notice.RetainedCitations.Contains(id, StringComparer.Ordinal))
                            notice.RetainedCitations.Add(id);

                trimmed = remaining;
                removed = true;
                break;
            }
            if (!removed)
                break;
        }

        var paragraphs = new List<string>();
        if (!string.IsNullOrWhiteSpace(trimmed))
            paragraphs.Add(trimmed);
        foreach (var notice in notices)
        {
            string[] ids = notice.Source is { } source ? [source.Id] : notice.RetainedCitations.ToArray();
            paragraphs.Add(notice.Rule.Text + string.Concat(ids.Select(id => $" [{id}]")));
        }
        return string.Join("\n\n", paragraphs);
    }

    private static List<ActiveNotice> ActiveNotices(RunContext context)
    {
        var notices = new List<ActiveNotice>();
        AddPolicy(WarrantyRule);
        AddPolicy(ReturnRule);

        // Permission, a model claim or a ticket from history is not a write result.
        // This boundary describes the current local ticket store, not a KB policy.
        if (context.Ticket is { Created: true, Ticket: not null })
            notices.Add(new(TicketRule, null, context.Sources.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal)));

        return notices;

        void AddPolicy(NoticeRule rule)
        {
            var selected = context.Sources.Where(hit => IsPolicySource(hit, rule.SourcePrefix!)).ToArray();
            if (selected.Length > 0)
                notices.Add(new(rule, selected[0], selected.Select(hit => hit.Id).ToHashSet(StringComparer.Ordinal)));
        }
    }

    private static bool TryRemoveTrailingNotice(string text, ActiveNotice notice,
        out string remaining, out string[] citations)
    {
        remaining = text;
        citations = [];
        var match = notice.Rule.Trailing.Match(text);
        if (!match.Success ||
            match.Index > 0 && !char.IsWhiteSpace(text[match.Index - 1]))
            return false;

        // Unknown citations and quoted occurrences must remain visible to validation.
        citations = match.Groups["sourceId"].Captures.Select(capture => capture.Value).ToArray();
        if (citations.Any(id => !notice.AllowedIds.Contains(id)))
            return false;

        string prefix = text[..match.Index].TrimEnd();
        bool startsLine = text.AsSpan(prefix.Length, match.Index - prefix.Length).Contains('\n');
        if (prefix.Length > 0 && !startsLine && !".!?]".Contains(prefix[^1]))
            return false;

        remaining = prefix;
        return true;
    }

    private static bool IsPolicySource(KnowledgeHit hit, string prefix) =>
        hit.Id.StartsWith(prefix, StringComparison.Ordinal) &&
        int.TryParse(hit.Id.AsSpan(prefix.Length),
            NumberStyles.None, CultureInfo.InvariantCulture, out int chunk) && chunk > 0;

    private sealed record NoticeRule(string Name, string Text, string? SourcePrefix)
    {
        // Match fixed trailing wording, including wrapped whitespace and optional
        // citations. This is not a semantic classifier for paraphrases.
        public Regex Trailing { get; } = new(
            Regex.Escape(Text).Replace(@"\ ", @"\s+") +
            @"(?:\s*\[(?<sourceId>[^\]\r\n]{1,100})\])*\s*\z",
            RegexOptions.CultureInvariant);
    }

    private sealed record ActiveNotice(NoticeRule Rule, KnowledgeHit? Source, HashSet<string> AllowedIds)
    {
        public List<string> RetainedCitations { get; } = [];
    }
}
