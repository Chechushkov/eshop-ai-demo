# ADR 0001: The model proposes actions; C# enforces authority

- Status: Accepted for the current implementation
- Date: 2026-10-08
- Scope: Support.API

## Context

The assistant reads policies and order status and can save demo tickets.
Questions, history, retrieved documents and model output may contain misleading
instructions. We need useful tool selection while keeping identity, ownership
and permission checks independent of model behavior.

## Decision

We execute actions through the allowlisted C# methods in
[SupportTools.cs](../../src/Support.API/SupportTools.cs). The model supplies only
a search query or ticket summary. User identity, credentials, selected order,
permission and request identifiers are excluded from tool arguments.

| Value | Source and validation |
| --- | --- |
| User identity and access token | The authenticated HTTP request; identity comes from the validated JWT. |
| Selected order | The current request; ownership is checked against Ordering.API results for that buyer. |
| `AllowTicket` | The current authenticated request; conversation text cannot grant permission. |
| Request and conversation IDs | The validated request; conversation ownership and the saved payload hash are checked in C#. |

`CreateSupportTicket` checks the current permission, request ID and summary,
then rereads the selected order before writing. In this eShop version,
[OrderingClient.cs](../../src/Support.API/OrderingClient.cs) uses the buyer-filtered
order list: the upstream order-by-ID query does not itself filter by buyer.

Ticket writes enforce `UNIQUE(user_id, order_id, request_id)`. Saved-response
replay uses `(conversation_id, request_id)` and a payload hash. Replay avoids
repeating completed work; ticket uniqueness protects writes after interrupted
processing. Matching question text alone is not a retry.

For new requests, C# completes missing retrieval and selected-order reads.
Currently, `AllowTicket=true` also requests ticket creation: the agent completes
an omitted action through the guarded tool, and the workflow uses graph edges.

History interprets follow-ups; fresh requests reread order facts and use the
current permission flag. Replays return the original snapshot. Known ticket IDs
come from stored tool results.

Reply validation checks source IDs and known ticket numbers. Invalid drafts get
one repair attempt, then a deterministic fallback from collected facts.
Valid citations do not establish semantic correctness.

## Alternatives considered

- Model-supplied authority arguments: rejected because generated values cannot
  establish caller permissions.
- Prompt-only permission checks: rejected because enforcement belongs at tool
  execution.
- A predetermined pipeline: viable; the agent mode lets us measure whether
  model-driven selection is useful here.

## Consequences

Both modes share authorization checks. Model text cannot grant ticket permission
or change the selected order through tool arguments.

Tools are limited to the current UI/API order selection. Ownership rechecks add
an Ordering.API call before writing. C# completion can hide model omissions;
evaluation must separate final success from model behavior. These guards do not
prevent every injection effect on search, summaries or wording.

## Planned ticket-policy change

Planned, not implemented: `AllowTicket` becomes permission; the model decides
whether a ticket is needed. Ownership, current-message permission and
idempotency checks remain mandatory. Preserve the current baseline as a tag and
measure unnecessary and missing tickets separately.

## Implementation references

- [Authentication](../../src/Support.API/Program.cs),
  [agent](../../src/Support.API/SupportAgent.cs),
  [workflow](../../src/Support.API/SupportWorkflow.cs).
- [Tickets](../../src/Support.API/KnowledgeRepository.cs),
  [conversations](../../src/Support.API/ConversationRepository.cs),
  [history](../../src/Support.API/ConversationMemory.cs).
- [Reply validation](../../src/Support.API/ReplyComposer.cs) and
  [offline checks](../../tests/Support.SelfTests/Program.cs).
