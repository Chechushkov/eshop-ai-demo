using eShop.ServiceDefaults;
using eShop.SupportApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Http.Resilience;

#pragma warning disable EXTEXP0001

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddDefaultAuthentication();

builder.Services.PostConfigure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidAudience = "orders";
    });

builder.AddNpgsqlDataSource("knowledge");

builder.Services.AddSingleton(new OpenAiSettings(
    builder.Configuration["OpenAI:ApiKey"] ?? "",
    builder.Configuration["OpenAI:Model"] ?? "gpt-5.4-mini",
    builder.Configuration["OpenAI:EmbeddingModel"]
        ?? "text-embedding-3-small"));

// Общий счётчик для обработки одного HTTP-запроса.
builder.Services.AddScoped<SupportMetricsCollector>();

builder.Services.AddHttpClient<OpenAiApi>(http =>
{
    http.BaseAddress = new Uri("https://api.openai.com/v1/");
    http.Timeout = TimeSpan.FromSeconds(180);
}).RemoveAllResilienceHandlers();

builder.Services.AddHttpClient<OrderingClient>(http =>
{
    http.BaseAddress = new Uri("https+http://ordering-api");
});

builder.Services.AddScoped<KnowledgeRepository>();

builder.Services.AddScoped<IKnowledgeSearch>(sp =>
    sp.GetRequiredService<KnowledgeRepository>());

builder.Services.AddScoped<ITicketStore>(sp =>
    sp.GetRequiredService<KnowledgeRepository>());

builder.Services.AddScoped<IOrderReader>(sp =>
    sp.GetRequiredService<OrderingClient>());

builder.Services.AddScoped<IResponsesModel>(sp =>
    sp.GetRequiredService<OpenAiApi>());

builder.Services.AddScoped<ReplyComposer>();
builder.Services.AddScoped<SupportAgent>();
builder.Services.AddScoped<SupportWorkflow>();

builder.Services.AddScoped<ConversationRepository>();

builder.Services.AddScoped<IConversationStore>(sp =>
    sp.GetRequiredService<ConversationRepository>());

builder.Services.AddScoped<SupportConversationService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var knowledge = scope.ServiceProvider
        .GetRequiredService<KnowledgeRepository>();

    await knowledge.EnsureSchemaAsync(
        app.Lifetime.ApplicationStopping);

    var conversations = scope.ServiceProvider
        .GetRequiredService<ConversationRepository>();

    await conversations.EnsureSchemaAsync(
        app.Lifetime.ApplicationStopping);

    await knowledge.ImportAsync(
        Path.Combine(app.Environment.ContentRootPath, "Knowledge"),
        app.Lifetime.ApplicationStopping);
}

// Список разговоров текущего пользователя.
app.MapGet(
    "/api/support/conversations",
    async Task<IResult> (
        HttpContext http,
        IConversationStore conversations) =>
    {
        string? userId = http.User.FindFirst("sub")?.Value;

        if (userId is null)
            return Results.Unauthorized();

        var list = await conversations.ListAsync(
            userId,
            http.RequestAborted);

        return Results.Ok(list);
    })
    .RequireAuthorization();

// История выбранного разговора.
app.MapGet(
    "/api/support/conversations/{id:guid}",
    async Task<IResult> (
        Guid id,
        HttpContext http,
        IConversationStore conversations) =>
    {
        string? userId = http.User.FindFirst("sub")?.Value;

        if (userId is null)
            return Results.Unauthorized();

        var conversation = await conversations.GetAsync(
            id,
            userId,
            http.RequestAborted);

        return conversation is null
            ? Results.NotFound()
            : Results.Ok(conversation);
    })
    .RequireAuthorization();

// Новое сообщение или повтор сохранённого запроса.
app.MapPost(
    "/api/support/ask",
    async Task<IResult> (
        SupportRequest request,
        HttpContext http,
        IKnowledgeSearch knowledge,
        IOrderReader orders,
        ITicketStore tickets,
        SupportConversationService conversations) =>
    {
        if (string.IsNullOrWhiteSpace(request.Question) ||
            request.Question.Length > 2000 ||
            request.RequestId == Guid.Empty ||
            request.ConversationId == Guid.Empty ||
            request.OrderId is <= 0 ||
            request.Mode is not ("agent" or "workflow"))
        {
            return Results.Problem(
                statusCode: 400,
                detail:
                    "Проверь вопрос (1..2000), orderId, requestId, " +
                    "conversationId и mode.");
        }

        string? userId = http.User.FindFirst("sub")?.Value;

        string authorization =
            http.Request.Headers.Authorization.ToString();

        if (userId is null ||
            !authorization.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.Unauthorized();
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                http.RequestAborted);

        timeout.CancelAfter(TimeSpan.FromSeconds(240));

        try
        {
            var result = await conversations.AskAsync(
                request,
                userId,
                authorization["Bearer ".Length..],
                knowledge,
                orders,
                tickets,
                timeout.Token);

            return Results.Ok(result);
        }
        catch (ConversationNotFoundException)
        {
            return Results.Problem(
                statusCode: 404,
                detail: "Разговор не найден. Начни новый разговор.");
        }
        catch (ConversationRequestConflictException error)
        {
            return Results.Problem(
                statusCode: 409,
                detail: error.Message);
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: 504,
                detail:
                    "Превышено время выполнения. " +
                    "Повтори запрос с тем же requestId.");
        }
        catch (Exception error)
            when (error is UpstreamException ||
                  error.InnerException is UpstreamException)
        {
            var safe = error as UpstreamException
                ?? (UpstreamException)error.InnerException!;

            return Results.Problem(
                statusCode: 502,
                detail: safe.Message);
        }
    })
    .RequireAuthorization();

app.Run();