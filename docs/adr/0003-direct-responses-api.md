# ADR 0003: Direct Responses API calls for the initial implementation

- Status: Accepted for the current implementation
- Date: 2026-10-08
- Scope: Support.API

## Context

The tutorial needs an inspectable model/tool exchange and offline tests that can
script model replies. We currently use one provider for replies and embeddings.
Provider portability is planned, but has not been implemented or evaluated.

We need to distinguish a replaceable client implementation from a neutral model
contract. An interface alone does not remove dependencies on a provider's data
format.

## Decision

Keep direct HTTP calls through
[OpenAiApi.cs](../../src/Support.API/OpenAiApi.cs) for the current baseline.
The client owns authentication, cancellation, request construction, response
parsing, error handling and usage collection.

The Responses request includes tool schemas, disables parallel tool calls and
requests encrypted reasoning content. The agent retains returned output items
within the current run and matches tool outputs using `call_id`. Requests set
`store=false`; this setting does not by itself establish a complete data-retention
policy.

[IResponsesModel](../../src/Support.API/Models.cs) is a testing and dependency
injection boundary. Its arguments contain JSON messages and tool definitions,
and its result is a `JsonObject` in the Responses API format.
[SupportAgent.cs](../../src/Support.API/SupportAgent.cs) still interprets
`function_call`, `arguments` and `function_call_output`. This contract is
provider-specific.

Keeping these exchanges explicit helps explain tool execution and diagnose model
omissions. It does not establish that direct HTTP is faster, cheaper or more
reliable than an SDK or a shared abstraction.

## Alternatives considered

- Official OpenAI SDK: delegates HTTP and protocol handling to a maintained
  client, while retaining an OpenAI-specific integration. We would still verify
  continuation items, tool-result correlation, cancellation and metrics.
- `IChatClient` from Microsoft.Extensions.AI: offers common chat request and
  response types behind adapters. It is a .NET contract, not a requirement to use
  a particular HTTP endpoint. Migration requires mapping our current semantics
  and checking the chosen adapter's capabilities.
- An application-owned neutral contract: can expose text, tool calls and usage
  without leaking Responses JSON into orchestration. It also makes us responsible
  for adapters and capability differences.

## Consequences

We own protocol compatibility, parsing and failure behavior. The OpenAI client
has automatic HTTP resilience handlers removed in
[Program.cs](../../src/Support.API/Program.cs). Reply repair is a separate model
call, not a transport retry.

The endpoint, model-name-based reasoning settings and usage field names also
couple this implementation to OpenAI. A server offering only Chat Completions
compatibility cannot satisfy this contract by an endpoint change alone.

Embeddings are a separate dependency:
[KnowledgeRepository.cs](../../src/Support.API/KnowledgeRepository.cs) calls
`OpenAiApi` directly and uses `vector(1536)`. Changing the embedding model requires
regenerating document vectors; changing dimensions also affects the schema.

Offline fakes validate selected application paths, not live API compatibility.

## Review trigger

Before introducing a second provider, choose `IChatClient` or a neutral application
contract and move provider JSON handling into an adapter. Verify tool-call IDs,
continuation state, output text, cancellation and usage accounting. Preserve the
authority checks from [ADR 0001](0001-model-proposes-code-decides.md) and compare
real-model outcomes using the evaluation planned in
[ADR 0002](0002-custom-agent-loop-and-framework-workflow.md).

## Implementation references

- [Reply composition](../../src/Support.API/ReplyComposer.cs) and
  [usage accounting](../../src/Support.API/SupportMetricsCollector.cs).
- [Offline checks](../../tests/Support.SelfTests/Program.cs).
