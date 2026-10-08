# ADR 0002: A custom agent loop and a framework workflow

- Status: Accepted for the current implementation
- Date: 2026-10-08
- Scope: Support.API

## Context

We need to compare model-selected actions with an application-defined process
while keeping the same tools, authorization checks and reply validation.
An agent describes who selects actions; a workflow describes how execution is
organized. These are separate choices.

The current support service has a custom C# agent loop and a graph built with
Microsoft Agent Framework. This ADR records that baseline, not a finding that
either mode has better quality, latency or cost.

## Decision

Keep both implementations for the initial comparison.

[SupportAgent.cs](../../src/Support.API/SupportAgent.cs) explicitly handles
Responses API items, invokes allowed tools and returns `function_call_output`
items to the model. Tool selection is limited to six model iterations. Retrieval
query rewriting and a final repair can add model calls outside that loop.
Keeping the loop visible makes protocol handling and model omissions inspectable.

[SupportWorkflow.cs](../../src/Support.API/SupportWorkflow.cs) uses
`WorkflowBuilder`, typed `Executor` nodes and conditional edges from
`Microsoft.Agents.AI.Workflows`. C# determines the route: read the order, retrieve
rules, optionally create a ticket, compose, validate, then finish or retry once.
The model writes the reply and may rewrite a retrieval query; it does not choose
the graph edges. The runner consumes events and requires exactly one final reply.

Both modes use the same `RunContext`, guarded `SupportTools` and `ReplyComposer`.
The authority boundary remains the one described in
[ADR 0001](0001-model-proposes-code-decides.md).

For the current baseline, C# completes missing agent retrieval and selected-order
reads. `AllowTicket=true` also requests ticket creation, which C# completes if the
agent omits it. Evaluation must distinguish model actions from C# completion.

Workflow execution is in process. PostgreSQL stores conversation history and
completed replies; this implementation does not persist graph checkpoints or
resume an interrupted run from an arbitrary node.

## Alternatives considered

- Framework-managed agent and framework workflow: could reduce application-owned
  orchestration. We would still need to verify tool-result handling, limits,
  permission checks and observable model omissions before migrating.
- Custom agent and custom C# workflow: removes the workflow runtime dependency
  and expresses branches and retries directly in application code. It gives up
  the typed graph and workflow-event exercise used in this tutorial.
- Workflow only: viable for the current process. It removes model-selected tool
  sequencing, so it cannot answer our planned comparison question.

## Consequences

We can inspect agent decisions and graph routing separately while sharing the
business rules. We maintain two orchestration implementations and own the
agent's provider-specific JSON handling. Framework events do not automatically
give us durable execution or prove business correctness.

The 23 offline checks verify selected paths with fake dependencies. They do not
establish real-model quality or a performance advantage for either mode.

## Review trigger

After preserving the baseline, evaluate the planned permission-only ticket
policy. Measure final outcomes, C# completion, repairs, latency and tokens.
Use those results to decide whether both modes remain useful or orchestration
should be consolidated.

## Implementation references

- [Tool guards](../../src/Support.API/SupportTools.cs) and
  [reply validation](../../src/Support.API/ReplyComposer.cs).
- [Conversation persistence](../../src/Support.API/ConversationRepository.cs)
  and [request replay](../../src/Support.API/ConversationMemory.cs).
- [Workflow package version](../../Directory.Packages.props) and
  [offline checks](../../tests/Support.SelfTests/Program.cs).
