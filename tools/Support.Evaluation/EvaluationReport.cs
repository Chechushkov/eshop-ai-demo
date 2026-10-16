using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace eShop.SupportEvaluation;

public sealed record LatencySummary(int Count, double? Median, long? P95NearestRank);
public sealed record ModeSummary(int TargetAttempts, Dictionary<string, int> Outcomes, int HttpErrors,
    int BehaviorObserved, int BehaviorUnknown, int ApplicationCompletedReads, int ReplyRecomposed,
    LatencySummary ClientWallMs, LatencySummary ServerElapsedMs, long? ModelCalls, long? EmbeddingCalls,
    long? InputTokens, long? OutputTokens, long? EmbeddingTokens, decimal? TargetCostEstimateUsd);
public sealed record EvaluationSummary(string RunId, string? Commit, bool? WorkingTreeDirty,
    Dictionary<string, ModeSummary> Modes, int PlannedRequests, int AttemptedRequests,
    decimal KnownCostEstimateUsd, decimal? TotalCostEstimateUsd, bool UsageUnknown, string? StopReason,
    int SetupRequests, decimal? SetupCostEstimateUsd, string QualityNote, string CostNote);

public static class EvaluationReport
{
    public static string Outcome(RunRecord record, ManualReview? review)
    {
        if (record.Kind == "skipped") return "invalid_setup";
        if (record.Checks is null || record.Checks.Errors.Length > 0) return "unsolved";
        if (review is null) return "pending_review";
        if (review.SemanticsPassed == false || review.TicketRowsVerified == false) return "unsolved";
        if (review.SemanticsPassed != true || review.TicketRowsVerified != true) return "pending_review";
        return Wire.Boolean(Wire.Field(record.Reply, "usedFallback")) == true ? "solved_via_fallback" : "solved";
    }

    public static LatencySummary Latency(IEnumerable<long?> values)
    {
        var ordered = values.Where(v => v is >= 0).Select(v => v!.Value).Order().ToArray();
        if (ordered.Length == 0) return new(0, null, null);
        int half = ordered.Length / 2;
        double median = ordered.Length % 2 == 1 ? ordered[half] : ordered[half - 1] / 2.0 + ordered[half] / 2.0;
        return new(ordered.Length, median, ordered[(int)Math.Ceiling(0.95 * ordered.Length) - 1]);
    }

    public static Dictionary<string, ManualReview> ReadReviews(string? path, IReadOnlySet<string> identifiers)
    {
        var reviews = new Dictionary<string, ManualReview>(StringComparer.Ordinal);
        if (path is null) return reviews;
        foreach (var review in ReadLines<ManualReview>(path))
        {
            EvaluationJson.Require(identifiers.Contains(review.RequestId), "A review references an unknown target request.");
            EvaluationJson.Require(review.Notes is not null, "Review notes must be a string.");
            EvaluationJson.Require(reviews.TryAdd(review.RequestId, review), "Duplicate review request ID.");
        }
        return reviews;
    }

    public static EvaluationSummary Save(string directory, string? reviewFile = null)
    {
        var records = ReadLines<RunRecord>(Path.Combine(directory, "responses.jsonl"));
        var manifest = EvaluationJson.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var status = EvaluationJson.Read<RunStatus>(Path.Combine(directory, "run_status.json"));
        var targets = records.Where(r => r.Kind is "target" or "skipped").ToArray();
        var identifiers = targets.Where(r => r.Kind == "target").Select(RequestId).ToHashSet(StringComparer.Ordinal);
        var reviews = ReadReviews(reviewFile, identifiers);
        var modes = Wire.Strings(Wire.Field(manifest, "modes")) ?? throw new EvaluationException("The manifest has no modes.");
        var grouped = new Dictionary<string, ModeSummary>(StringComparer.Ordinal);
        foreach (string mode in modes)
        {
            var selected = targets.Where(r => r.Mode == mode).ToArray();
            var executed = selected.Where(r => r.Kind == "target").ToArray();
            var states = selected.GroupBy(r => Outcome(r, r.Kind == "target" ? reviews.GetValueOrDefault(RequestId(r)) : null))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var observations = executed.Select(r => ReplyChecks.Behavior(r.Reply)).ToArray();
            grouped[mode] = new(executed.Length, states, executed.Count(r => r.HttpStatus != 200),
                observations.Count(v => v is not null), observations.Count(v => v is null),
                observations.Count(v => v is not null && v.AutoCompletedActions.Length > 0),
                observations.Count(v => v?.ReplyRecomposed == true), Latency(executed.Select(r => r.WallMs)),
                Latency(executed.Select(r => Metric(r, "elapsedMs"))),
                SumMetric(executed, "modelCalls"), SumMetric(executed, "embeddingCalls"),
                SumMetric(executed, "inputTokens"), SumMetric(executed, "outputTokens"),
                SumMetric(executed, "embeddingTokens"), SumCost(executed));
        }
        var setup = records.Where(r => r.Kind == "setup").ToArray();
        var summary = new EvaluationSummary(Wire.Text(manifest["run_id"]) ?? "unknown", Wire.Text(manifest["commit"]),
            Wire.Boolean(manifest["working_tree_dirty"]), grouped, checked((int)(Wire.Integer(manifest["planned_requests"]) ?? 0)),
            status.AttemptedRequests, status.KnownCostEstimateUsd, status.TotalCostEstimateUsd,
            status.UsageUnknown, status.StopReason, setup.Length, SumCost(setup),
            "Quality is determined by automatic checks, semantic review and database row verification. See the outcome counts and review results.",
            "Estimated standard token cost, with all input billed at the uncached rate; not an invoice.");
        Artifacts.Json(Path.Combine(directory, "summary.json"), summary);
        Artifacts.Write(Path.Combine(directory, "summary.md"), Markdown(summary, targets, reviews));
        return summary;
    }

    public static IReadOnlyList<T> ReadLines<T>(string path) => !File.Exists(path) ? [] : File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => EvaluationJson.Deserialize<T>(EvaluationJson.Parse(line))).ToArray();

    private static string RequestId(RunRecord record) => Wire.Text(record.Request?["requestId"])
        ?? throw new EvaluationException("A target has no request identifier.");
    private static long? Metric(RunRecord record, string field) => Wire.NonnegativeInteger(Wire.Field(Wire.Field(record.Reply, "metrics"), field));
    private static long? SumMetric(IEnumerable<RunRecord> records, string field)
    {
        var values = records.Select(r => Metric(r, field)).ToArray();
        if (values.Any(v => v is null)) return null;
        try { return values.Sum(v => v!.Value); }
        catch (OverflowException) { return null; }
    }
    private static decimal? SumCost(IEnumerable<RunRecord> records)
    {
        var values = records.Select(r => r.CostEstimateUsd).ToArray();
        return values.Any(v => v is null) ? null : values.Sum(v => v!.Value);
    }

    private static string Show(object? value) => value is null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture)!;
    private static string Markdown(EvaluationSummary summary, RunRecord[] targets, Dictionary<string, ManualReview> reviews)
    {
        var text = new StringBuilder("# Support evaluation summary\n\n");
        text.AppendLine($"Run: `{summary.RunId}`\n");
        text.AppendLine(summary.QualityNote + "\n");
        text.AppendLine("| Mode | Attempts | Solved | Fallback solved | Unsolved | Pending | Invalid setup | Read completion | Recomposed | Unknown behavior |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (mode, data) in summary.Modes)
        {
            int Count(string key) => data.Outcomes.GetValueOrDefault(key);
            text.AppendLine($"| {mode} | {data.TargetAttempts} | {Count("solved")} | {Count("solved_via_fallback")} | " +
                $"{Count("unsolved")} | {Count("pending_review")} | {Count("invalid_setup")} | {data.ApplicationCompletedReads} | {data.ReplyRecomposed} | {data.BehaviorUnknown} |");
        }
        text.AppendLine("\n## Target latency and usage\n");
        text.AppendLine("| Mode | Wall median, ms | Wall p95, ms | Model calls | Input tokens | Output tokens | Estimated USD |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (mode, data) in summary.Modes)
            text.AppendLine($"| {mode} | {Show(data.ClientWallMs.Median)} | {Show(data.ClientWallMs.P95NearestRank)} | " +
                $"{Show(data.ModelCalls)} | {Show(data.InputTokens)} | {Show(data.OutputTokens)} | {Show(data.TargetCostEstimateUsd)} |");
        text.AppendLine("\np95 uses nearest rank; this small pilot does not establish reliable tail latency.\n");
        text.AppendLine($"Setup requests: {summary.SetupRequests}; estimated setup USD: {Show(summary.SetupCostEstimateUsd)}.");
        text.AppendLine($"Total estimated USD: {Show(summary.TotalCostEstimateUsd)}; known subtotal: {Show(summary.KnownCostEstimateUsd)}.\n");
        text.AppendLine("Missing usage is unknown, not zero. Cost estimates use the uncached input rate.\n");
        text.AppendLine($"Stop reason: {summary.StopReason ?? "None"}\n\n## Review results\n");
        foreach (var record in targets)
        {
            string? id = record.Kind == "target" ? RequestId(record) : null;
            text.AppendLine($"- {record.ScenarioId} / {record.Mode} / {record.Repeat}: " +
                $"{Outcome(record, id is null ? null : reviews.GetValueOrDefault(id))}" + (id is null ? "." : $"; request `{id}`."));
            foreach (var error in record.Checks?.Errors ?? []) text.AppendLine("  - Automatic check: " + error);
            foreach (var flag in record.Checks?.ReviewFlags ?? []) text.AppendLine("  - Review flag: " + flag);
        }
        return text.ToString();
    }
}
