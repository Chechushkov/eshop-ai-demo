using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace eShop.SupportEvaluation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        return await EvaluationCli.RunAsync(args, Console.Out, Console.Error, stop.Token);
    }
}

public static class EvaluationCli
{
    private static readonly HashSet<string> Flags = ["--execute", "--preflight-only", "--no-browser"];
    private static readonly HashSet<string> Values = ["--root", "--fixtures", "--scenarios", "--knowledge-dir", "--expected-count",
        "--prices", "--only", "--modes", "--repeats", "--max-requests", "--budget-usd", "--request-headroom-usd",
        "--support-url", "--ordering-url", "--buyer-token-file", "--foreign-token-file", "--results-dir",
        "--authority", "--token-file", "--run-dir", "--reviews"];

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors, CancellationToken ct = default)
    {
        try
        {
            if (args.Any(a => a is "--help" or "-h"))
            {
                output.WriteLine("Support evaluation (.NET 10): validate | plan | run | login | summarize");
                output.WriteLine("Default: plan, without HTTP requests. Paid run requires --execute --budget-usd AMOUNT.");
                output.WriteLine("run: --preflight-only or --execute; --buyer-token-file FILE --foreign-token-file FILE");
                output.WriteLine("run/plan: --only IDS --modes agent workflow --repeats N --max-requests N --prices FILE");
                output.WriteLine("login: --no-browser --token-file FILE [--authority http://localhost:5223]");
                output.WriteLine("summarize: --run-dir DIR [--reviews FILE]");
                return 0;
            }
            string command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "plan";
            var options = Parse(command == "plan" && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) ? args : args.Skip(1).ToArray());
            string? Optional(string key) => options.TryGetValue(key, out var value) ? value.Single() : null;
            string Required(string key) => Optional(key) ?? throw new EvaluationException($"{key} is required.");
            string Get(string key, string fallback) => Optional(key) ?? fallback;
            bool Has(string key) => options.ContainsKey(key);
            if (command == "login")
            {
                await BuyerLogin.LoginAsync(Get("--authority", "http://localhost:5223"), Required("--token-file"), Has("--no-browser"), ct);
                return 0;
            }
            if (command == "summarize")
            {
                var summary = EvaluationReport.Save(Required("--run-dir"), Optional("--reviews"));
                output.WriteLine(EvaluationJson.Serialize(summary));
                return 0;
            }
            EvaluationJson.Require(command is "validate" or "plan" or "run", "Unknown evaluation command.");
            string root = Optional("--root") is { } selectedRoot ? Path.GetFullPath(selectedRoot) : FindRoot();
            string dataset = Get("--scenarios", Path.Combine(root, "eval/scenarios.jsonl"));
            int expectedCount = PositiveInt(Get("--expected-count", "10"));
            var all = ScenarioContract.Load(dataset,
                ScenarioContract.DocumentIds(Get("--knowledge-dir", Path.Combine(root, "src/Support.API/Knowledge"))), expectedCount);
            string fixturePath = Get("--fixtures", Path.Combine(root, "eval/fixtures.local.json"));
            if (command == "validate")
            {
                if (Has("--fixtures")) ScenarioContract.Fixtures(EvaluationJson.Parse(File.ReadAllText(fixturePath)));
                output.WriteLine($"OK: {all.Count} scenarios validated. No APIs or models were called.");
                output.WriteLine($"One run in both modes: {2 * all.Count} target requests + {2 * all.Sum(s => s.Setup.Length)} setup requests.");
                output.WriteLine(Has("--fixtures") ? "Order ID format checked; existence and ownership still require verification." : "Order fixtures were not checked.");
                return 0;
            }
            var scenarios = all;
            if (Optional("--only") is { } selection)
            {
                var ids = selection.Split(',');
                EvaluationJson.Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length && ids.All(id => all.Any(s => s.Id == id)),
                    "Unknown or duplicate selected scenario ID.");
                scenarios = all.Where(s => ids.Contains(s.Id)).ToArray();
            }
            string[] modes = options.GetValueOrDefault("--modes") ?? ["agent", "workflow"];
            EvaluationJson.Require(modes.Length > 0 && modes.All(m => m is "agent" or "workflow") && modes.Distinct().Count() == modes.Length,
                "Modes must be distinct agent/workflow values.");
            int repeats = PositiveInt(Get("--repeats", "1")), maximum = PositiveInt(Get("--max-requests", "22"));
            EvaluationJson.Require(repeats <= 5, "repeats must be between 1 and 5.");
            int planned = checked(scenarios.Sum(s => s.Setup.Length + 1) * modes.Length * repeats);
            EvaluationJson.Require(planned <= maximum, $"Planned {planned} requests exceed max-requests={maximum}.");
            output.WriteLine($"Plan: {scenarios.Count} scenarios, modes={string.Join(',', modes)}, repeats={repeats}, HTTP requests including setup={planned}.");
            if (command == "plan" || !Has("--execute") && !Has("--preflight-only"))
            {
                if (Has("--fixtures") || File.Exists(fixturePath)) ScenarioContract.Fixtures(EvaluationJson.Parse(File.ReadAllText(fixturePath)));
                output.WriteLine("Dry run complete. No HTTP requests or model calls were made.");
                return 0;
            }
            EvaluationJson.Require(!(Has("--execute") && Has("--preflight-only")), "Use either execute or preflight-only.");
            var fixtures = ScenarioContract.Fixtures(EvaluationJson.Parse(File.ReadAllText(fixturePath)));
            decimal headroom = PositiveMoney(Get("--request-headroom-usd", "0.10"));
            decimal limit = Has("--execute") ? PositiveMoney(Required("--budget-usd")) : headroom;
            EvaluationJson.Require(limit >= headroom, "Budget is below request headroom.");
            var buyer = new BuyerCredential(Required("--buyer-token-file"));
            var foreign = new BuyerCredential(Required("--foreign-token-file"));
            string supportUrl = Get("--support-url", "http://localhost:5260"), orderingUrl = Get("--ordering-url", "http://localhost:5224");
            using var support = new EvaluationApi(supportUrl, buyer);
            using var ordering = new EvaluationApi(orderingUrl, buyer);
            using var otherOrdering = new EvaluationApi(orderingUrl, foreign);
            var preflight = await OwnershipPreflight.VerifyAsync(support, ordering, otherOrdering, fixtures, ct);
            output.WriteLine("Preflight passed: both orders exist, ownership differs, and Support.API accepts the buyer token.");
            if (Has("--preflight-only"))
            {
                output.WriteLine("No support questions or model calls were made.");
                return 0;
            }
            string pricePath = Get("--prices", Path.Combine(root, "eval/prices.json"));
            var prices = PriceTable.Load(pricePath);
            string name = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            string directory = Path.Combine(Get("--results-dir", Path.Combine(root, "eval/results")), name);
            EvaluationJson.Require(!Directory.Exists(directory), "The result directory already exists.");
            Artifacts.CreateDirectory(directory);
            var revision = await GitRevision(root, ct);
            Artifacts.Json(Path.Combine(directory, "manifest.json"), new JsonObject
            {
                ["schema_version"] = 1, ["run_id"] = name, ["commit"] = revision.Commit, ["working_tree_dirty"] = revision.Dirty,
                ["scenario_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataset))).ToLowerInvariant(),
                ["scenarios"] = new JsonArray(scenarios.Select(s => (JsonNode?)JsonValue.Create(s.Id)).ToArray()),
                ["modes"] = new JsonArray(modes.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
                ["repeats"] = repeats, ["planned_requests"] = planned,
                ["fixtures"] = EvaluationJson.Parse(EvaluationJson.Serialize(fixtures)),
                ["order_preflight"] = EvaluationJson.Parse(EvaluationJson.Serialize(preflight)),
                ["prices"] = EvaluationJson.Parse(File.ReadAllText(pricePath)), ["budget_usd"] = limit,
                ["request_headroom_usd"] = headroom, ["max_requests"] = maximum,
                ["support_url"] = supportUrl, ["ordering_url"] = orderingUrl
            });
            var result = await new EvaluationRunner(output).RunAsync(scenarios, fixtures, modes, repeats, support, prices,
                new RunBudget(limit, headroom, maximum), directory, [foreign.Value], ct);
            EvaluationReport.Save(directory);
            output.WriteLine("Results saved to " + directory);
            if (result.Status.StopReason is { } reason) errors.WriteLine(reason);
            return result.Status.StopReason is not null || result.Records.Any(r => r.Kind == "skipped" || r.Checks?.Errors.Length > 0) ? 2 : 0;
        }
        catch (OperationCanceledException) { errors.WriteLine("Cancelled."); return 130; }
        catch (Exception error) when (error is EvaluationException or IOException or UnauthorizedAccessException or OverflowException)
        { errors.WriteLine("ERROR: " + error.Message); return 1; }
    }

    private static Dictionary<string, string[]> Parse(string[] args)
    {
        var options = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string key = args[index];
            EvaluationJson.Require(!options.ContainsKey(key), "Duplicate option: " + key);
            if (Flags.Contains(key)) { options.Add(key, []); continue; }
            EvaluationJson.Require(Values.Contains(key), "Unknown option: " + key);
            var values = new List<string>();
            while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values.Add(args[++index]);
                if (key != "--modes") break;
            }
            EvaluationJson.Require(values.Count > 0, "Missing value for " + key);
            options.Add(key, values.ToArray());
        }
        return options;
    }

    private static int PositiveInt(string value)
    {
        EvaluationJson.Require(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0,
            "Use a positive integer.");
        return number;
    }
    private static decimal PositiveMoney(string value)
    {
        EvaluationJson.Require(decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number) && number > 0,
            "Use a positive decimal amount with a dot.");
        return number;
    }

    private static string FindRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "eval/scenarios.jsonl"))) return directory.FullName;
        }
        throw new EvaluationException("Run from the repository or provide --root.");
    }

    private static async Task<(string? Commit, bool? Dirty)> GitRevision(string root, CancellationToken ct)
    {
        async Task<string?> Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            try
            {
                using var process = Process.Start(start)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct), stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                await stderr;
                return process.ExitCode == 0 ? (await stdout).Trim() : null;
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
        string? commit = await Git("rev-parse", "HEAD"), dirty = await Git("status", "--porcelain");
        return (commit, dirty is null ? null : dirty.Length > 0);
    }
}
