# Structured model-behavior observations

Support replies include an optional `behavior` object assigned by C# execution
code. Evaluation should read this object rather than parse execution-trace text.
These observations describe the original generation of the reply.

```json
{
  "usedFallback": false,
  "behavior": {
    "autoCompletedActions": ["GetOrderStatus"],
    "replyRecomposed": true
  }
}
```

## Field meanings

| Field | Meaning |
| --- | --- |
| `autoCompletedActions` | Reads performed by the application's completion block after the agent's selection loop. Values are `SearchKnowledge` and `GetOrderStatus`, in execution order. |
| `replyRecomposed` | The agent entered its additional composition path, or the workflow executed its reply-retry node. |
| `usedFallback` | The existing independent flag identifying a deterministic final reply built from confirmed facts. |

For the agent, additional composition can follow missing facts, failed validation
or exhaustion of the selection-loop limit. A valid repaired reply has
`replyRecomposed=true` and `usedFallback=false`.

Automatic completion measures application intervention. An empty retrieval
result can trigger completion even when the model already called search. It is
therefore not sufficient evidence that the model skipped a tool. Order rechecking
inside `CreateSupportTicket` is not automatic completion.

Workflow reads are predefined graph steps and leave `autoCompletedActions` empty.
Retrieval-query rewriting and ticket classification are model calls, but they do
not set `replyRecomposed`. Only the second reply composition does so. Compare
these observations with the request's mode and final outcome; they do not by
themselves measure answer correctness or establish which mode is better.

## History and replay

New replies snapshot the run's observations before persistence. Mutating the run
context later cannot change the saved reply's action list. The existing JSONB
reply storage carries the added object without a database-schema change.

Older replies that lack the object deserialize with `behavior=null`. This means
unknown observations; evaluation must not convert them into an empty list and
`false`. The support page displays this distinction in its trace panel.

Replay retains the original `behavior` while its request metrics report
`isReplay=true` and zero model calls. Do not count a replay as a fresh evaluation
run. Use new request identifiers when measuring current generation behavior.

## Verification

```bash
dotnet build tests/Support.SelfTests/Support.SelfTests.csproj --no-restore
dotnet run --project tests/Support.SelfTests --no-build --no-restore -- --behavior-only
```

The offline checks cover model-selected and completed reads, repair, fallback,
selection-loop exhaustion, workflow retries, JSON compatibility, client decoding
and replay preservation. They use fake model and storage dependencies. PostgreSQL
integration and real-model quality need separate checks.

Implementation: [API contract](../src/Support.API/Models.cs),
[agent](../src/Support.API/SupportAgent.cs),
[workflow](../src/Support.API/SupportWorkflow.cs),
[reply snapshot](../src/Support.API/ReplyComposer.cs) and
[checks](../tests/Support.SelfTests/ModelBehaviorTests.cs).
