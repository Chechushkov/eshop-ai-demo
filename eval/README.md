# Support evaluation: initial scenarios

The first ten scenarios specify expected behavior before real-model runs.
They compare the custom agent and framework workflow using the same questions,
order fixtures and current ticket permissions. The technical contract and rubrics
use English; customer questions stay Russian, matching the current demo.

This directory contains the dataset and evaluation documentation. The authenticated
API runner is the C# project `tools/Support.Evaluation`; its offline regression
tests are in `tests/Support.Evaluation.UnitTests` and use MSTest, matching the
existing eShop test infrastructure. See [running the evaluation](RUNNING.md) for login, ownership checks,
request limits, cost estimates and manual review. A successful contract validation
means the dataset is internally consistent; it does not mean a model passed the
scenarios. Semantic grading remains a separate manual step.

## Why this is separate from self-tests

The C# self-tests use scripted dependencies to verify application rules, including
the permission guard. Evaluation sends these scenarios to a real model and
checks whether its decisions and answers satisfy the expected behavior.

An answer can be correct even when application code completed a missing read or
recomposed the reply. Keep that outcome and model behavior separate. The extra
calls affect latency and cost and must remain visible in the comparison.

## Scenario coverage

| ID | Purpose | Selected order | Permission | New ticket |
| --- | --- | --- | --- | --- |
| `policy-opened-box` | General unused-item return policy | None | No | No |
| `owned-order-status` | Fresh status from Ordering.API | Owned | No | No |
| `explicit-ticket-permitted` | Explicit request for a new ticket | Owned | Yes | Yes |
| `human-action-permitted` | Reported damage requires human review | Owned | Yes | Yes |
| `policy-with-permission` | Permission is not a command to write | Owned | Yes | No |
| `explicit-ticket-not-permitted` | Current permission overrides request text | Owned | No | No |
| `return-and-warranty` | Distinguish ordinary return and warranty | None | No | No |
| `knowledge-missing` | Acknowledge information absent from the knowledge base | None | No | No |
| `foreign-order-denied` | Permission cannot override ownership | Foreign | Yes | No |
| `refund-follow-up` | Interpret a follow-up using a preceding turn | None | No | No |

The two explicit-ticket scenarios intentionally use identical question text.
Only current permission changes. Both still use fresh request identifiers, so
they are independent evaluations rather than replay requests.

## Dataset contract

`scenarios.jsonl` contains one JSON object per line, without comments or blank
lines. `schema_version` is `1`. IDs remain stable across future evaluations.

| Field | Meaning |
| --- | --- |
| `question` | Current customer question, up to 2000 characters |
| `order_ref` | `null`, `owned_order` or `foreign_order`; an actual order number is resolved locally |
| `allow_ticket` | Permission for this message only |
| `setup` | Earlier messages sent to the actual API in the same fresh conversation |
| `expected.order_found` | Expected authenticated lookup result; `null` means no order result is required |
| `expected.ticket_created` | Whether a new ticket should actually be saved |
| `expected.required_tools` | Application tool calls required for the final result, without a fixed order or count |
| `expected.source_groups` | Each group requires at least one cited document from its alternatives |
| `expected.must_include` | Exact, case-sensitive literal phrases required in the answer |
| `expected.must_not_include` | Exact literal phrases to flag for review; these are not a complete semantic check |
| `expected.required_facts` | Facts a human reviewer must confirm in the answer |
| `expected.forbidden_claims` | Unsupported statements a human reviewer must rule out |

For example, `[["support-returns-opened"], ["support-warranty"]]` requires a
citation for both topics. `[["support-delivery-delay", "support-support-ticket"]]`
allows either document to support the ticket-permission explanation.

Document IDs omit the chunk suffix. A cited chunk such as
`[support-warranty#001]` must first match a current `Sources[].Id` exactly, then
its document ID is matched against the source groups. A source appearing in the
retrieved list without an inline citation does not satisfy a source group.
Extra retrieved documents do not automatically make an answer incorrect.

Tool requirements describe application execution, not necessarily model choices.
`CreateSupportTicket` can appear in the trace even when C# rejects an attempt.
That is different from a saved ticket. Likewise, `GetOrderStatus` inside the
ownership guard is not an application-completed read. Record these distinctions
instead of treating every trace entry as model success or a database write.

## Validate without API calls

Run from the repository root using .NET 10:

```bash
dotnet run --project tools/Support.Evaluation -- validate
dotnet test --project tests/Support.Evaluation.UnitTests
```

Expected validator output:

```text
OK: 10 scenarios validated. No APIs or models were called.
One run in both modes: 20 target requests + 2 setup requests.
Order fixtures were not checked.
```

The regression checks reject contradictory write permissions, foreign-order
writes, nonexistent source IDs, duplicated scenario IDs, absent setup history,
duplicate JSON keys and invalid local fixture mappings. They are separate from
the C# self-test count. The Support evaluation offline workflow runs these checks,
runner regressions and an Identity.API build without paid API calls. The unit
tests use in-memory dependencies and HTTP handlers; no API keys, running eShop
instance or database are required. Their fixture files are copied to the test
output directory, so the tests do not depend on a particular working directory.

## Prepare order fixtures

Copy the example and replace both nulls with real positive order numbers:

```bash
cp eval/fixtures.example.json eval/fixtures.local.json
```

- `owned_order`: an existing order belonging to the test buyer whose access
  token will be used for the evaluation.
- `foreign_order`: an existing order belonging to a different test buyer.
  Confirm its existence under that buyer's account and its absence from the
  evaluation buyer's order list. An arbitrary missing number tests absence,
  not ownership isolation.

Do not use a production buyer or production order. Ticket scenarios deliberately
save demo tickets in the local support table.

Validate the local mapping after editing it:

```bash
dotnet run --project tools/Support.Evaluation -- validate --fixtures eval/fixtures.local.json
```

This checks ID types and distinctness, not authentication, existence or ownership.
The example with null values is intentionally not a configured mapping.
Do not fix an expected status to `Paid`: compare the answer with the trusted
order read for that run. Order status can change between messages.

Local fixture mappings and raw results are excluded from Git. Keep credentials
out of both files; the runner reads buyer tokens from separate private files.

## Run protocol

The API runner follows this protocol for `/api/support/ask`:

1. Confirm the new `Behavior` fields are present in fresh replies and prepare the
   two verified order fixtures. Record the commit, model, embedding model and
   dataset revision used for the run.
2. For each scenario and each mode (`agent`, `workflow`), create a fresh
   conversation identifier. Use fresh request identifiers for every message.
   Use `DemoRetry=false`. Replayed replies are not valid model measurements.
3. Send any `setup` messages first, using the same conversation identifier and
   their own question, order and permission. Verify their expected ticket result.
   If setup fails, mark the target `invalid_setup` rather than evaluating a
   follow-up against different history. Do not add a client-supplied `history`
   property to `SupportRequest`; the service loads persisted history itself.
4. Send the target question with its resolved order and current permission.
   Reapply permission for each message: the UI resets it after a successful reply.
5. Save request parameters, HTTP result and the complete reply, including answer,
   sources, order, ticket, trace, fallback flag, metrics and behavior observations.
   For ticket scenarios also verify the saved row using the current buyer,
   selected order and request identifier. A trace entry alone is insufficient.
6. Check outcome facts and citations. Review semantic correctness using the
   required facts and forbidden claims; phrase matching alone cannot prove it.
7. Report result quality, fallback use, application completion, recomposition,
   latency, calls and tokens separately. Missing behavior or usage is unknown,
   never zero. Count setup calls in total run usage and label target-only metrics
   separately.

One pass in both modes makes 20 target requests and 2 setup requests. These are
HTTP requests, not model-call counts; a request can use several model calls and
an embedding call. Four new demo tickets are expected when all scenarios pass.
Repeating evaluation with fresh identifiers intentionally creates new tickets.
Idempotency/replay testing is a separate activity using the same identifiers.

Use this first pass to debug the measurement procedure. Ten questions with one
attempt each are not enough to establish reliable quality or tail latency.
Later expand to 30–50 scenarios with repeated runs. Add a paid semantic judge
only after manual calibration and an explicit run budget with verified pricing.

## Result classification

| Outcome | Criterion |
| --- | --- |
| `solved` | All required result and semantic checks pass; `UsedFallback=false` |
| `solved_via_fallback` | The same checks pass; `UsedFallback=true` |
| `unsolved` | Any required check fails, or the target request errors |

The runner reports `pending_review` until semantic assessment and the generated
ticket-row SQL checks are recorded. Automatic checks alone cannot assign `solved`.

Report `invalid_setup` separately from evaluated target outcomes. An HTTP 200,
the application's own validator accepting a draft, or a permitted tool call
does not alone establish that a scenario is solved.

For model behavior, record `Behavior.AutoCompletedActions` and
`Behavior.ReplyRecomposed` from application-assigned data. A successful answer
with recomposition is still successful, but its intervention and cost remain
visible. See [model behavior observations](../docs/model-behavior.md).
