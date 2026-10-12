# C# evaluation tools and tests

The evaluation implementation is part of the .NET solution. JSONL contains
customer inputs and expected outcomes; it is test data rather than executable
test code. Customer questions remain Russian. Test names, comments, diagnostics
and engineering documentation use English.

| Location | Responsibility |
| --- | --- |
| `tools/Support.Evaluation/Contracts.cs` | Read strict JSON, reject duplicate keys and contradictory scenarios |
| `ReplyChecks.cs` | Check citations, tool execution, orders, tickets and dated token-price estimates |
| `ApiClient.cs` | Read buyer credentials, call local APIs and verify both order owners |
| `BuyerLogin.cs` | Obtain a buyer token through authorization code flow with PKCE |
| `EvaluationRunner.cs` | Run scenarios sequentially, preserve history and save attempted requests |
| `EvaluationReport.cs` | Summarize latency, usage and manually reviewed outcomes |
| `Artifacts.cs` | Write private token and result files |
| `Program.cs` | Parse console commands; default to a plan without HTTP requests |
| `tests/Support.Evaluation.UnitTests` | MSTest regressions for the evaluation implementation |
| `tests/Support.SelfTests` | Existing checks for the support application, including ticket guards |

## Two types of verification

An offline unit test checks a rule under controlled inputs. For example, it can
simulate a lost HTTP reply and verify that the runner saves the request once and
stops before issuing another one. The tests supply in-memory dependencies;
HTTP transport tests inject `HttpMessageHandler` and exercise the real
`HttpClient` adapter without opening a network connection.

A real-model evaluation checks how the complete application behaves for a
customer question. The runner calls Support.API, which uses its configured
model, real order reads and PostgreSQL. Those runs are explicit and budgeted.
Passing offline tests does not imply that the model answered correctly.

The current MSTest suite has 38 test methods. It covers the original 33 Python
regressions and adds HTTP transport and CLI checks. Some related conditions are
asserted within the same method. The JSONL dataset has not changed.

## Typical unit test

Tests use Arrange, Act and Assert. This example checks the dataset contract:

```csharp
[TestMethod]
public void DuplicateJsonKeyCannotHidePermission()
{
    const string input = "{\"allow_ticket\":false,\"allow_ticket\":true}";

    Assert.ThrowsExactly<EvaluationException>(
        () => EvaluationJson.Parse(input));
}
```

Duplicate keys matter because different JSON readers can otherwise disagree on
which permission value is effective. The validator rejects the ambiguity before
a run. Separately, the application's ticket guard still checks current
permission and ownership before writing; an evaluation expectation cannot grant
permission to the application.

The runner tests distinguish a tool attempt from a saved ticket.
`CreateSupportTicket` in a trace can represent a rejected call. The response
must report the expected creation result, and a real run still needs the
generated database query to verify persisted rows, including requests expected
to create no ticket.

## Build and test

From the repository root:

```bash
dotnet restore tests/Support.Evaluation.UnitTests/Support.Evaluation.UnitTests.csproj
dotnet test --project tests/Support.Evaluation.UnitTests --minimum-expected-tests 38
dotnet run --project tools/Support.Evaluation -- validate
dotnet run --project tools/Support.Evaluation -- plan
```

The existing `global.json` selects Microsoft.Testing.Platform and pins
`MSTest.Sdk`; the new project follows that configuration. In this mode, use the
`--project` option with `dotnet test`. The minimum count prevents empty test
discovery from looking successful. Both projects are included in the solution
and the web solution filter.

References: [MSTest SDK](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-sdk)
and [dotnet test with Microsoft.Testing.Platform](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test).

For authentication, model requests, pricing and review instructions, see
[running the evaluation](RUNNING.md). CI runs only validation, offline tests and
builds. It never performs buyer login or model requests.
