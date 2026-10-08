using Microsoft.Agents.AI.Workflows;

namespace eShop.SupportApi;

public sealed class SupportWorkflow(ReplyComposer composer)
{
    public async Task<SupportReply> RunAsync(
        RunContext state,
        SupportTools tools,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var load = new LoadOrder(tools, state);
        var retrieve = new RetrieveRules(tools, state, composer);
        var ticket = new CreateTicket(tools, state);
        var reply = new ComposeReply(composer, state);
        var validate = new ValidateReply(state);
        var retry = new RetryReply(composer, state);
        var finish = new FinishReply(state);

        var graph = new WorkflowBuilder(load)
            .AddEdge(load, retrieve)

            .AddEdge<SupportRequest>(
                retrieve,
                ticket,
                condition: _ =>
                    state.Request.AllowTicket &&
                    state.Order is { Found: true })

            .AddEdge<SupportRequest>(
                retrieve,
                reply,
                condition: _ =>
                    !state.Request.AllowTicket ||
                    state.Order is not { Found: true })

            .AddEdge(ticket, reply)
            .AddEdge(reply, validate)

            .AddEdge<ReplyDraft>(
                validate,
                retry,
                condition: x => x is { NeedsRetry: true })

            .AddEdge<ReplyDraft>(
                validate,
                finish,
                condition: x => x is { NeedsRetry: false })

            .AddEdge(retry, validate)
            .WithOutputFrom(finish)
            .Build();

        state.Log("workflow start");
        state.LogRequest();

        await using var run =
            await InProcessExecution.RunStreamingAsync(
                graph,
                state.Request,
                cancellationToken: ct);

        SupportReply? result = null;

        try
        {
            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                if (evt is ExecutorFailedEvent failed)
                {
                    ct.ThrowIfCancellationRequested();

                    throw new InvalidOperationException(
                        $"Workflow executor {failed.ExecutorId} failed.",
                        failed.Data);
                }

                if (evt is WorkflowErrorEvent error)
                {
                    ct.ThrowIfCancellationRequested();

                    throw new InvalidOperationException(
                        "The workflow failed.",
                        error.Exception);
                }

                if (evt is WorkflowOutputEvent
                    { Data: SupportReply output })
                {
                    if (result is not null)
                    {
                        throw new InvalidOperationException(
                            "The workflow produced multiple replies.");
                    }

                    result = output;
                }
            }

            ct.ThrowIfCancellationRequested();

            return result
                ?? throw new InvalidOperationException(
                    "The workflow completed without a SupportReply.");
        }
        finally
        {
            if (ct.IsCancellationRequested)
                await run.CancelRunAsync();
        }
    }

    private sealed class LoadOrder(
        SupportTools tools,
        RunContext state)
        : Executor<SupportRequest, SupportRequest>("load_order")
    {
        public override async ValueTask<SupportRequest> HandleAsync(
            SupportRequest request,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            state.Log("workflow load_order");

            if (request.OrderId is not null)
                await tools.GetOrderStatusAsync(cancellationToken);

            return request;
        }
    }

    private sealed class RetrieveRules(
        SupportTools tools,
        RunContext state,
        ReplyComposer composer)
        : Executor<SupportRequest, SupportRequest>("retrieve_rules")
    {
        public override async ValueTask<SupportRequest> HandleAsync(
            SupportRequest request,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            state.Log("workflow retrieve_rules");

            // Resolve history references before knowledge search.
            // Use the original question for the first message.
            string query =
                await composer.ResolveRetrievalQueryAsync(
                    state,
                    cancellationToken);

            await tools.SearchKnowledgeAsync(
                query,
                cancellationToken);

            return request;
        }
    }

    private sealed class CreateTicket(
        SupportTools tools,
        RunContext state)
        : Executor<SupportRequest, SupportRequest>("create_ticket")
    {
        public override async ValueTask<SupportRequest> HandleAsync(
            SupportRequest request,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            state.Log("workflow create_ticket");

            string summary = request.Question[
                ..Math.Min(request.Question.Length, 1000)];

            await tools.CreateSupportTicketAsync(
                summary,
                cancellationToken);

            return request;
        }
    }

    private sealed class ComposeReply(
        ReplyComposer composer,
        RunContext state)
        : Executor<SupportRequest, ReplyDraft>("reply")
    {
        public override async ValueTask<ReplyDraft> HandleAsync(
            SupportRequest request,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            state.Log("workflow reply attempt=1");

            string text = await composer.ComposeAsync(
                state,
                [],
                cancellationToken);

            return new ReplyDraft(text, 1, []);
        }
    }

    private sealed class ValidateReply(RunContext state)
        : Executor<ReplyDraft, ReplyDraft>("validate_reply")
    {
        public override ValueTask<ReplyDraft> HandleAsync(
            ReplyDraft draft,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var errors = ReplyComposer
                .Validate(draft.Text, state)
                .ToList();

            if (state.Request.DemoRetry && draft.Attempt == 1)
            {
                errors.Add(
                    "Demo validation: the first draft was rejected. " +
                    "Rewrite the reply using the confirmed facts.");
            }

            state.Log(
                $"workflow validate_reply attempt={draft.Attempt} " +
                $"errors={errors.Count}");

            state.LogValidation(
                $"workflow validation attempt={draft.Attempt}",
                errors.ToArray());

            return ValueTask.FromResult(
                draft with { Errors = errors.ToArray() });
        }
    }

    private sealed class RetryReply(
        ReplyComposer composer,
        RunContext state)
        : Executor<ReplyDraft, ReplyDraft>("retry_reply")
    {
        public override async ValueTask<ReplyDraft> HandleAsync(
            ReplyDraft draft,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            state.Log("workflow retry_reply attempt=2");

            string text = await composer.ComposeAsync(
                state,
                draft.Errors,
                cancellationToken);

            return new ReplyDraft(text, 2, []);
        }
    }

    private sealed class FinishReply(RunContext state)
        : Executor<ReplyDraft, SupportReply>("finish")
    {
        public override ValueTask<SupportReply> HandleAsync(
            ReplyDraft draft,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool fallback = draft.Errors.Length > 0;

            state.Log(
                fallback
                    ? "workflow finish fallback"
                    : "workflow finish OK");

            return ValueTask.FromResult(
                ReplyComposer.Finish(
                    draft.Text,
                    state,
                    fallback));
        }
    }
}