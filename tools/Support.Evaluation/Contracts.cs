using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace eShop.SupportEvaluation;

public class EvaluationException(string message) : Exception(message);

public sealed record ScenarioTurn(string Question, string? OrderRef, bool AllowTicket,
    bool ExpectedTicketCreated);

public sealed record ExpectedOutcome(bool? OrderFound, bool TicketCreated,
    string[] RequiredTools, string[][] SourceGroups, string[] MustInclude,
    string[] MustNotInclude, string[] RequiredFacts, string[] ForbiddenClaims);

public sealed record Scenario(int SchemaVersion, string Id, string Category,
    string Question, string? OrderRef, bool AllowTicket, ScenarioTurn[] Setup,
    ExpectedOutcome Expected)
{
    public ScenarioTurn Target => new(Question, OrderRef, AllowTicket, Expected.TicketCreated);
}

public sealed record OrderFixtures(int OwnedOrder, int ForeignOrder)
{
    public int? Resolve(string? reference) => reference switch
    {
        null => null,
        "owned_order" => OwnedOrder,
        "foreign_order" => ForeignOrder,
        _ => throw new EvaluationException("Unknown order fixture reference.")
    };
}

public static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false
    };

    public static JsonNode Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('\uFEFF'));
            CheckDuplicates(document.RootElement);
            return JsonNode.Parse(document.RootElement.GetRawText())
                ?? throw new EvaluationException("JSON must not be null.");
        }
        catch (JsonException error)
        {
            throw new EvaluationException($"Invalid JSON at {error.Path ?? "root"}.");
        }
    }

    public static T Read<T>(string path) => Deserialize<T>(Parse(File.ReadAllText(path)));

    public static T Deserialize<T>(JsonNode node)
    {
        try
        {
            return node.Deserialize<T>(Options)
                ?? throw new EvaluationException("JSON must not be null.");
        }
        catch (JsonException error)
        {
            throw new EvaluationException($"Invalid JSON contract at {error.Path ?? "root"}.");
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), $"Duplicate JSON key: {property.Name}");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new EvaluationException(message);
    }
}

// Read wire values without coercing strings or booleans into numbers.
public static class Wire
{
    public static JsonNode? Field(JsonNode? node, string key) => (node as JsonObject)?[key];
    public static string? Text(JsonNode? node) => node is JsonValue value &&
        value.TryGetValue<string>(out var text) ? text : null;
    public static bool? Boolean(JsonNode? node) => node is JsonValue value &&
        value.TryGetValue<bool>(out var result) ? result : null;
    public static long? Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var small)) return small;
        return null;
    }
    public static long? NonnegativeInteger(JsonNode? node) => Integer(node) is long number && number >= 0 ? number : null;
    public static string[]? Strings(JsonNode? node)
    {
        if (node is not JsonArray array || array.Any(item => Text(item) is null)) return null;
        return array.Select(item => Text(item)!).ToArray();
    }
}

public static class ScenarioContract
{
    private static readonly string[] Categories = ["general_policy", "owned_order", "explicit_ticket",
        "human_action", "permission_without_need", "ticket_without_permission", "multiple_topics",
        "missing_knowledge", "foreign_order", "history_follow_up"];
    private static readonly string[] Tools = ["SearchKnowledge", "GetOrderStatus", "CreateSupportTicket"];
    private static readonly string[] Fields = ["schema_version", "id", "category", "question",
        "order_ref", "allow_ticket", "setup", "expected"];
    private static readonly string[] ExpectedFields = ["order_found", "ticket_created", "required_tools",
        "source_groups", "must_include", "must_not_include", "required_facts", "forbidden_claims"];
    public const string NoNewTicket = "Новая заявка не создана.";

    public static IReadOnlyList<Scenario> Load(string path, IReadOnlySet<string> documents, int expectedCount = 10)
    {
        var lines = File.ReadAllLines(path);
        var nodes = new List<JsonNode>();
        foreach (var line in lines)
        {
            EvaluationJson.Require(!string.IsNullOrWhiteSpace(line), "Blank JSONL records are not allowed.");
            nodes.Add(EvaluationJson.Parse(line));
        }
        Validate(nodes, documents, expectedCount);
        return nodes.Select(EvaluationJson.Deserialize<Scenario>).ToArray();
    }

    public static HashSet<string> DocumentIds(string directory)
    {
        EvaluationJson.Require(Directory.Exists(directory), "Knowledge directory does not exist.");
        var ids = Directory.GetFiles(directory, "*.md")
            .Select(path => "support-" + Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.Ordinal);
        EvaluationJson.Require(ids.Count > 0, "The knowledge directory contains no Markdown documents.");
        return ids;
    }

    public static void Validate(IReadOnlyList<JsonNode> scenarios, IReadOnlySet<string> documents, int expectedCount = 10)
    {
        EvaluationJson.Require(expectedCount > 0 && scenarios.Count == expectedCount,
            $"Expected {expectedCount} scenarios, received {scenarios.Count}.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in scenarios)
        {
            ExactFields(node, Fields, "Scenario");
            EvaluationJson.Require(Wire.Integer(node["schema_version"]) == 1, "schema_version must be 1.");
            string? id = Wire.Text(node["id"]);
            EvaluationJson.Require(id is not null && Regex.IsMatch(id, "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z"), "Invalid scenario ID.");
            EvaluationJson.Require(ids.Add(id!), $"Duplicate scenario id: {id}");
            string? category = Wire.Text(node["category"]);
            EvaluationJson.Require(category is not null && Categories.Contains(category), $"{id}: unknown category.");
            categories.Add(category!);
            ValidateInput(node);
            EvaluationJson.Require(node["setup"] is JsonArray, $"{id}: setup must be an array.");
            var setup = (JsonArray)node["setup"]!;
            foreach (var turn in setup)
            {
                ExactFields(turn, ["question", "order_ref", "allow_ticket", "expected_ticket_created"], "Setup");
                ValidateInput(turn!);
                ValidateTicket(Wire.Boolean(turn!["expected_ticket_created"]), turn);
            }
            EvaluationJson.Require(category != "history_follow_up" || setup.Count > 0, "A history follow-up requires setup turns.");
            var expected = node["expected"];
            ExactFields(expected, ExpectedFields, "Expected outcome");
            ValidateTicket(Wire.Boolean(expected!["ticket_created"]), node);
            bool? found = Wire.Text(node["order_ref"]) switch { "owned_order" => true, "foreign_order" => false, _ => null };
            EvaluationJson.Require(expected["order_found"] is null || Wire.Boolean(expected["order_found"]) is not null,
                "order_found must be null or a boolean.");
            EvaluationJson.Require(Wire.Boolean(expected["order_found"]) == found, "order_found contradicts the selected fixture.");
            foreach (string field in new[] { "required_tools", "must_include", "must_not_include", "required_facts", "forbidden_claims" })
                TextList(expected[field], field, field is "required_tools" or "required_facts" or "forbidden_claims");
            var tools = Wire.Strings(expected["required_tools"])!;
            EvaluationJson.Require(tools.All(Tools.Contains), "Unknown required tool.");
            EvaluationJson.Require(tools.Contains("SearchKnowledge"), "A knowledge read is required.");
            EvaluationJson.Require(node["order_ref"] is null || tools.Contains("GetOrderStatus"), "The selected order must be read.");
            EvaluationJson.Require(Wire.Boolean(expected["ticket_created"]) != true || tools.Contains("CreateSupportTicket"), "A created ticket requires its tool.");
            var included = Wire.Strings(expected["must_include"])!;
            EvaluationJson.Require(!included.Intersect(Wire.Strings(expected["must_not_include"])!).Any(), "Conflicting literal phrase expectations.");
            EvaluationJson.Require(Wire.Boolean(node["allow_ticket"]) != true || Wire.Boolean(expected["ticket_created"]) == true ||
                included.Contains(NoNewTicket), "Include the stable no-new-ticket sentence.");
            EvaluationJson.Require(expected["source_groups"] is JsonArray, "source_groups must be an array.");
            foreach (var group in (JsonArray)expected["source_groups"]!)
            {
                TextList(group, "Source group", true);
                EvaluationJson.Require(Wire.Strings(group)!.All(documents.Contains), "Source group contains unknown document IDs.");
            }
        }
        EvaluationJson.Require(categories.SetEquals(Categories), "Required scenario categories are missing.");
    }

    public static OrderFixtures Fixtures(JsonNode node)
    {
        ExactFields(node, ["owned_order", "foreign_order"], "Fixtures");
        long? owned = Wire.Integer(node["owned_order"]), foreign = Wire.Integer(node["foreign_order"]);
        EvaluationJson.Require(owned is > 0 and <= int.MaxValue && foreign is > 0 and <= int.MaxValue,
            "Fixtures must contain positive C# int order IDs.");
        EvaluationJson.Require(owned != foreign, "The owned and foreign order IDs must differ.");
        return new((int)owned!.Value, (int)foreign!.Value);
    }

    private static void ExactFields(JsonNode? node, string[] fields, string label)
    {
        EvaluationJson.Require(node is JsonObject obj && obj.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(fields),
            $"{label}: missing or unknown fields.");
    }

    private static void ValidateInput(JsonNode node)
    {
        string? question = Wire.Text(node["question"]), reference = Wire.Text(node["order_ref"]);
        EvaluationJson.Require(!string.IsNullOrWhiteSpace(question) && question.Length <= 2000, "Question must contain 1 to 2000 characters.");
        EvaluationJson.Require(node["order_ref"] is null || reference is "owned_order" or "foreign_order", "Invalid order_ref.");
        EvaluationJson.Require(Wire.Boolean(node["allow_ticket"]) is not null, "allow_ticket must be a boolean.");
        EvaluationJson.Require(Wire.Boolean(node["allow_ticket"]) != true || reference is not null, "Select an order when enabling permission.");
    }

    private static void ValidateTicket(bool? created, JsonNode node)
    {
        EvaluationJson.Require(created is not null, "ticket_created must be a boolean.");
        EvaluationJson.Require(created != true || Wire.Boolean(node["allow_ticket"]) == true && Wire.Text(node["order_ref"]) == "owned_order",
            "A ticket expectation requires current permission and an owned order.");
    }

    private static void TextList(JsonNode? node, string label, bool nonempty)
    {
        var values = Wire.Strings(node);
        EvaluationJson.Require(values is not null && values.All(v => !string.IsNullOrWhiteSpace(v)) &&
            values.Distinct(StringComparer.Ordinal).Count() == values.Length && (!nonempty || values.Length > 0),
            $"{label}: expected unique, nonblank strings.");
    }
}
