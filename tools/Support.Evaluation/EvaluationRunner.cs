using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace eShop.SupportEvaluation;

public sealed record RunRecord
{
    public required string Kind { get; init; }
    public required string ScenarioId { get; init; }
    public required string Mode { get; init; }
    public int Repeat { get; init; }
    public JsonObject? Request { get; init; }
    public bool? ExpectedTicketCreated { get; init; }
    public ExpectedOutcome? Expected { get; init; }
    public int? HttpStatus { get; init; }
    public JsonNode? Reply { get; init; }
    public string? Error { get; init; }
    public long? WallMs { get; init; }
    public decimal? CostEstimateUsd { get; init; }
    public CheckResult? Checks { get; init; }
    public bool? SetupValid { get; init; }
    public string? Reason { get; init; }
}

public sealed record RunStatus(int AttemptedRequests, decimal KnownCostEstimateUsd,
    decimal? TotalCostEstimateUsd, bool UsageUnknown, string? StopReason);
public sealed record ManualReview(string RequestId, bool? SemanticsPassed, bool? TicketRowsVerified, string Notes);
public sealed record RunResult(IReadOnlyList<RunRecord> Records, RunStatus Status);

public sealed class EvaluationRunner(TextWriter? output = null)
{
    private readonly TextWriter output = output ?? Console.Out;

    public static JsonObject Payload(ScenarioTurn turn, OrderFixtures fixtures, string mode, Guid conversation) => new()
    {
        ["question"] = turn.Question, ["orderId"] = fixtures.Resolve(turn.OrderRef),
        ["allowTicket"] = turn.AllowTicket, ["requestId"] = Guid.NewGuid().ToString("D"),
        ["conversationId"] = conversation.ToString("D"), ["mode"] = mode, ["demoRetry"] = false
    };

    public async Task<RunResult> RunAsync(IReadOnlyList<Scenario> scenarios, OrderFixtures fixtures,
        string[] modes, int repeats, IEvaluationApi client, PriceTable prices, RunBudget budget,
        string directory, IReadOnlyList<string>? extraSecrets = null, CancellationToken ct = default)
    {
        var records = new List<RunRecord>();
        string? stopReason = null;
        try
        {
            for (int repeat = 1; repeat <= repeats; repeat++)
            foreach (var scenario in scenarios)
            foreach (var mode in repeat % 2 == 1 ? modes : modes.Reverse())
            {
                // Each scenario/mode gets independent history and fresh identifiers.
                Guid conversation = Guid.NewGuid();
                bool setupOk = true;
                var turns = scenario.Setup.Select(turn => (Kind: "setup", Turn: turn))
                    .Append(("target", scenario.Target));
                foreach (var (kind, turn) in turns)
                {
                    if (kind == "target" && !setupOk)
                    {
                        var skipped = new RunRecord { Kind = "skipped", ScenarioId = scenario.Id,
                            Mode = mode, Repeat = repeat, Reason = "invalid_setup" };
                        records.Add(skipped);
                        Artifacts.Append(Path.Combine(directory, "responses.jsonl"), skipped);
                        continue;
                    }
                    ct.ThrowIfCancellationRequested();
                    client.Credential.EnsureValid();
                    budget.Reserve();
                    var request = Payload(turn, fixtures, mode, conversation);
                    var basis = new RunRecord { Kind = kind, ScenarioId = scenario.Id, Mode = mode,
                        Repeat = repeat, Request = request, ExpectedTicketCreated = turn.ExpectedTicketCreated };
                    // Save the identifiers before POST; a lost reply must never cause an automatic retry.
                    Artifacts.Append(Path.Combine(directory, "requests.jsonl"), basis);
                    ApiResponse response;
                    try { response = await client.RequestAsync("/api/support/ask", request, extraSecrets, ct); }
                    catch (OperationCanceledException)
                    {
                        response = new(null, null, "Interrupted; the server outcome is unknown.", null);
                        stopReason = "Interrupted";
                    }
                    catch (EvaluationException)
                    { response = new(null, null, "Token validation failed before the HTTP request.", null); }
                    var metrics = Wire.Field(response.Reply, "metrics");
                    decimal? cost = prices.Estimate(metrics);
                    budget.Record(cost);
                    CheckResult? checks = null;
                    if (kind == "target")
                    {
                        checks = ReplyChecks.Check(response.Reply, scenario, fixtures.Resolve(turn.OrderRef));
                        var errors = checks.Errors.ToList();
                        if (response.HttpStatus != 200 || response.Error is not null)
                            errors.Add(response.Error ?? "The target HTTP request failed.");
                        if (response.Reply is JsonObject && Wire.Text(response.Reply["conversationId"]) != conversation.ToString("D"))
                            errors.Add("The reply has a different conversation identifier.");
                        checks = checks with { Errors = errors.ToArray() };
                    }
                    else
                    {
                        setupOk = response.HttpStatus == 200 && response.Error is null &&
                            ReplyChecks.TicketCreated(response.Reply) == turn.ExpectedTicketCreated &&
                            !string.IsNullOrWhiteSpace(Wire.Text(Wire.Field(response.Reply, "answer"))) &&
                            Wire.Text(Wire.Field(response.Reply, "conversationId")) == conversation.ToString("D") &&
                            Wire.Boolean(Wire.Field(metrics, "isReplay")) == false;
                    }
                    var record = basis with
                    {
                        HttpStatus = response.HttpStatus, Reply = response.Reply, Error = response.Error,
                        WallMs = response.WallMs, CostEstimateUsd = cost, Checks = checks,
                        Expected = kind == "target" ? scenario.Expected : null,
                        SetupValid = kind == "setup" ? setupOk : null
                    };
                    records.Add(record);
                    Artifacts.Append(Path.Combine(directory, "responses.jsonl"), record);
                    output.WriteLine($"{kind}: {scenario.Id} / {mode} / {repeat}; HTTP={response.HttpStatus?.ToString() ?? "unknown"}; " +
                        $"estimated USD={cost?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
                    if (budget.KnownCost > budget.Limit) stopReason = "Stopped: the completed request exceeded the estimated budget.";
                    if (budget.UnknownUsage || stopReason is not null)
                        throw new EvaluationException(stopReason ?? "Stopped: missing usage, model-price mismatch or replay.");
                }
            }
        }
        catch (EvaluationException error) { stopReason = error.Message; }
        catch (OperationCanceledException) { stopReason = "Interrupted before starting another request."; }
        finally
        {
            WriteTicketSql(directory, records);
            foreach (var record in records.Where(record => record.Kind == "target"))
                Artifacts.Append(Path.Combine(directory, "reviews.example.jsonl"),
                    new ManualReview(Wire.Text(record.Request!["requestId"])!, null, null, ""));
            Artifacts.Json(Path.Combine(directory, "run_status.json"),
                new RunStatus(budget.Attempts, budget.KnownCost, budget.UnknownUsage ? null : budget.KnownCost,
                    budget.UnknownUsage, stopReason));
        }
        return new(records, new(budget.Attempts, budget.KnownCost, budget.UnknownUsage ? null : budget.KnownCost,
            budget.UnknownUsage, stopReason));
    }

    public static void WriteTicketSql(string directory, IReadOnlyList<RunRecord> records)
    {
        var rows = records.Where(r => r.Kind is "setup" or "target").Select(record =>
        {
            EvaluationJson.Require(Guid.TryParse(Wire.Text(record.Request?["requestId"]), out var id), "Invalid request identifier for SQL verification.");
            return $"('{id:D}'::uuid, {(record.ExpectedTicketCreated == true ? "true" : "false")})";
        }).ToArray();
        if (rows.Length == 0) return;
        string sql = "-- Read-only verification: a denied request must have zero rows.\n" +
            "WITH expected(request_id, should_create) AS (VALUES\n  " + string.Join(",\n  ", rows) + "\n)\n" +
            "SELECT e.request_id, e.should_create, count(t.id) AS actual_rows,\n" +
            "       array_agg(t.id) FILTER (WHERE t.id IS NOT NULL) AS ticket_ids,\n" +
            "       array_agg(t.order_id) FILTER (WHERE t.id IS NOT NULL) AS order_ids,\n" +
            "       array_agg(t.user_id) FILTER (WHERE t.id IS NOT NULL) AS buyer_ids,\n" +
            "       count(t.id) = CASE WHEN e.should_create THEN 1 ELSE 0 END AS count_matches\n" +
            "FROM expected e LEFT JOIN support_tickets t ON t.request_id = e.request_id\n" +
            "GROUP BY e.request_id, e.should_create ORDER BY e.request_id;\n";
        Artifacts.Write(Path.Combine(directory, "verify_tickets.sql"), sql);
    }
}
