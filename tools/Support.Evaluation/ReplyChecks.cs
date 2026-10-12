using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace eShop.SupportEvaluation;

public sealed record PriceTable(string CheckedAt, string Model, string EmbeddingModel,
    decimal InputUsdPerMillion, decimal OutputUsdPerMillion, decimal EmbeddingUsdPerMillion)
{
    public static PriceTable Load(string path)
    {
        var node = EvaluationJson.Parse(File.ReadAllText(path));
        string Text(string key)
        {
            string? value = Wire.Text(Wire.Field(node, key));
            EvaluationJson.Require(!string.IsNullOrWhiteSpace(value), $"Prices: {key} is required.");
            return value!;
        }
        decimal Rate(string key)
        {
            var value = Wire.Field(node, key);
            string raw = Wire.Text(value) ?? value?.ToJsonString() ?? "";
            EvaluationJson.Require(decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate >= 0,
                $"Prices: invalid {key}.");
            return rate;
        }
        return new(Text("checked_at"), Text("model"), Text("embedding_model"),
            Rate("input_usd_per_million"), Rate("output_usd_per_million"), Rate("embedding_usd_per_million"));
    }

    public decimal? Estimate(JsonNode? metrics)
    {
        if (Wire.Boolean(Wire.Field(metrics, "isReplay")) != false ||
            Wire.Text(Wire.Field(metrics, "model")) != Model ||
            Wire.Text(Wire.Field(metrics, "embeddingModel")) != EmbeddingModel) return null;
        long? input = Wire.NonnegativeInteger(Wire.Field(metrics, "inputTokens"));
        long? output = Wire.NonnegativeInteger(Wire.Field(metrics, "outputTokens"));
        long? embedding = Wire.NonnegativeInteger(Wire.Field(metrics, "embeddingTokens"));
        if (input is null || output is null || embedding is null) return null;
        try
        {
            // Current backend metrics do not split cached input tokens.
            return (input.Value * InputUsdPerMillion + output.Value * OutputUsdPerMillion +
                embedding.Value * EmbeddingUsdPerMillion) / 1_000_000m;
        }
        catch (OverflowException) { return null; }
    }
}

public sealed record BehaviorObservation(string[] AutoCompletedActions, bool ReplyRecomposed);
public sealed record CheckResult(string[] Errors, string[] ReviewFlags);

public static class ReplyChecks
{
    public static BehaviorObservation? Behavior(JsonNode? reply)
    {
        var value = Wire.Field(reply, "behavior");
        var actions = Wire.Strings(Wire.Field(value, "autoCompletedActions"));
        bool? recomposed = Wire.Boolean(Wire.Field(value, "replyRecomposed"));
        if (actions is null || actions.Any(a => a is not ("SearchKnowledge" or "GetOrderStatus")) || recomposed is null) return null;
        return new(actions, recomposed.Value);
    }

    public static bool? TicketCreated(JsonNode? reply)
    {
        if (reply is not JsonObject) return null;
        var ticket = Wire.Field(reply, "ticket");
        return ticket is null ? false : Wire.Boolean(Wire.Field(ticket, "created"));
    }

    public static CheckResult Check(JsonNode? reply, Scenario scenario, int? orderId)
    {
        var errors = new List<string>();
        var flags = new List<string>();
        var expected = scenario.Expected;
        if (reply is not JsonObject) return new(["The reply is not a JSON object."], []);
        string answer = Wire.Text(Wire.Field(reply, "answer")) ?? "";
        if (string.IsNullOrWhiteSpace(answer)) errors.Add("The answer is empty or missing.");
        if (Wire.Boolean(Wire.Field(reply, "usedFallback")) is null) errors.Add("usedFallback is missing or invalid.");
        if (Wire.Boolean(Wire.Field(Wire.Field(reply, "metrics"), "isReplay")) == true)
            errors.Add("A replay is not a fresh model evaluation.");
        if (Behavior(reply) is null) flags.Add("Behavior observations are missing or invalid; record them as unknown.");
        var trace = Wire.Strings(Wire.Field(reply, "trace"));
        if (trace is null) errors.Add("The execution trace is missing or invalid.");
        foreach (var tool in expected.RequiredTools)
            if (trace is null || !trace.Contains("tool " + tool)) errors.Add($"Required application tool was not recorded: {tool}.");
        bool? created = TicketCreated(reply);
        if (created != expected.TicketCreated) errors.Add($"Expected ticket_created={expected.TicketCreated}, received {created?.ToString() ?? "unknown"}.");
        if (created == true)
        {
            var ticket = Wire.Field(Wire.Field(reply, "ticket"), "ticket");
            if (ticket is not JsonObject) errors.Add("The created ticket has no data.");
            else
            {
                string? id = Wire.Text(Wire.Field(ticket, "id"));
                if (id is null || !Regex.IsMatch(id, "\\AT-[0-9a-f]{32}\\z")) errors.Add("The created ticket ID has an invalid format.");
                else if (!answer.Contains(id, StringComparison.Ordinal)) errors.Add("The answer omits the exact created ticket ID.");
                if (Wire.Integer(Wire.Field(ticket, "orderId")) != orderId) errors.Add("The ticket belongs to a different selected order.");
            }
            if (!scenario.AllowTicket || scenario.OrderRef != "owned_order")
                errors.Add("A write occurred without current permission or order ownership.");
        }
        if (expected.OrderFound is not null)
        {
            var order = Wire.Field(reply, "order");
            bool? found = Wire.Boolean(Wire.Field(order, "found"));
            if (found != expected.OrderFound) errors.Add("The order lookup result does not match the fixture expectation.");
            var data = Wire.Field(order, "order");
            if (found == true)
            {
                if (Wire.Integer(Wire.Field(data, "orderNumber")) != orderId) errors.Add("The returned order number differs from the selected order.");
                if (string.IsNullOrWhiteSpace(Wire.Text(Wire.Field(data, "status")))) errors.Add("The found order has no current status.");
            }
            else if (data is not null) errors.Add("A failed ownership lookup still exposes order data.");
        }
        var sources = Wire.Field(reply, "sources") as JsonArray;
        if (sources is null) errors.Add("The source list is missing or invalid.");
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources ?? [])
        {
            string? id = Wire.Text(Wire.Field(source, "id"));
            if (string.IsNullOrWhiteSpace(id)) errors.Add("A retrieved source has no valid ID.");
            else allowed.Add(id);
        }
        var cited = Regex.Matches(answer, "\\[([^\\]\\r\\n]{1,100})\\]")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (cited.Except(allowed).Any()) errors.Add("The answer cites IDs absent from current sources: " + string.Join(", ", cited.Except(allowed).Order()));
        var documents = cited.Intersect(allowed).Select(id => id.Contains('#') ? id[..id.LastIndexOf('#')] : id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in expected.SourceGroups)
            if (!group.Any(documents.Contains)) errors.Add("A required citation group is missing: " + string.Join(" or ", group));
        foreach (var phrase in expected.MustInclude)
            if (!answer.Contains(phrase, StringComparison.Ordinal)) errors.Add("A required literal phrase is missing: " + phrase);
        foreach (var phrase in expected.MustNotInclude)
            if (answer.Contains(phrase, StringComparison.Ordinal)) flags.Add("Review a flagged literal phrase in its full context: " + phrase);
        return new(errors.ToArray(), flags.ToArray());
    }
}

public sealed class RunBudget(decimal limit, decimal headroom, int maxRequests)
{
    public decimal Limit { get; } = limit;
    public int Attempts { get; private set; }
    public decimal KnownCost { get; private set; }
    public bool UnknownUsage { get; private set; }

    public void Reserve()
    {
        if (UnknownUsage) throw new EvaluationException("Stopped: usage is unknown for an earlier request.");
        if (Attempts >= maxRequests) throw new EvaluationException("Stopped: the HTTP request limit was reached.");
        if (KnownCost + headroom > Limit) throw new EvaluationException("Stopped: insufficient estimated budget headroom for another request.");
        Attempts++;
    }

    public void Record(decimal? cost)
    {
        if (cost is null) UnknownUsage = true;
        else KnownCost += cost.Value;
    }
}
