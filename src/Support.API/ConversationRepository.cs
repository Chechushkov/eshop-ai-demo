using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace eShop.SupportApi;

public sealed class ConversationRepository(
    NpgsqlDataSource dataSource) : IConversationStore
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS support_conversations (
                id uuid PRIMARY KEY,
                user_id text NOT NULL,
                order_id integer,
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );

            CREATE TABLE IF NOT EXISTS support_conversation_turns (
                conversation_id uuid NOT NULL
                    REFERENCES support_conversations(id),
                request_id uuid NOT NULL,
                request_hash text NOT NULL,
                turn_id bigserial NOT NULL UNIQUE,
                request_json jsonb NOT NULL,
                reply_json jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY(conversation_id, request_id)
            );

            CREATE INDEX IF NOT EXISTS support_conversation_turns_history
                ON support_conversation_turns(
                    conversation_id, turn_id DESC
                );

            CREATE INDEX IF NOT EXISTS support_conversations_recent
                ON support_conversations(
                    user_id, updated_at DESC, id DESC
                );
            """);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IConversationLease> OpenAsync(
        SupportRequest request,
        string userId,
        CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct);
        NpgsqlTransaction? transaction = null;

        try
        {
            transaction = await connection.BeginTransactionAsync(ct);

            await using (var create = new NpgsqlCommand("""
                INSERT INTO support_conversations(id, user_id)
                VALUES(@id, @user)
                ON CONFLICT(id) DO NOTHING
                """, connection, transaction))
            {
                // Общий лимит запроса задаётся CancellationToken.
                create.CommandTimeout = 0;

                create.Parameters.AddWithValue(
                    "id", request.ConversationId!.Value);
                create.Parameters.AddWithValue("user", userId);

                await create.ExecuteNonQueryAsync(ct);
            }

            // Блокировка обеспечивает последовательную обработку
            // сообщений одного разговора, даже на нескольких API-инстансах.
            await using (var owner = new NpgsqlCommand("""
                SELECT id
                FROM support_conversations
                WHERE id=@id AND user_id=@user
                FOR UPDATE
                """, connection, transaction))
            {
                owner.CommandTimeout = 0;

                owner.Parameters.AddWithValue(
                    "id", request.ConversationId!.Value);
                owner.Parameters.AddWithValue("user", userId);

                if (await owner.ExecuteScalarAsync(ct) is null)
                    throw new ConversationNotFoundException();
            }

            string hash = ConversationMemory.RequestHash(request);
            SupportReply? cached = null;

            await using (var repeat = new NpgsqlCommand("""
                SELECT request_hash, reply_json::text
                FROM support_conversation_turns
                WHERE conversation_id=@id AND request_id=@request
                """, connection, transaction))
            {
                repeat.Parameters.AddWithValue(
                    "id", request.ConversationId!.Value);
                repeat.Parameters.AddWithValue(
                    "request", request.RequestId);

                await using var reader =
                    await repeat.ExecuteReaderAsync(ct);

                if (await reader.ReadAsync(ct))
                {
                    if (!string.Equals(
                        reader.GetString(0),
                        hash,
                        StringComparison.Ordinal))
                    {
                        throw new ConversationRequestConflictException();
                    }

                    cached = JsonSerializer.Deserialize<SupportReply>(
                        reader.GetString(1), Json)
                        ?? throw new InvalidOperationException(
                            "Не удалось прочитать сохранённый ответ.");
                }
            }

            var history = cached is null
                ? await ReadTurnsAsync(
                    connection,
                    transaction,
                    request.ConversationId!.Value,
                    ct)
                : [];

            return new Lease(
                connection,
                transaction,
                request,
                hash,
                history,
                cached);
        }
        catch
        {
            try
            {
                if (transaction is not null)
                    await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }

            throw;
        }
    }

    public async Task<ConversationView?> GetAsync(
        Guid conversationId,
        string userId,
        CancellationToken ct)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(ct);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, ct);

        int? orderId;

        await using (var owner = new NpgsqlCommand("""
            SELECT order_id
            FROM support_conversations
            WHERE id=@id AND user_id=@user
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue("id", conversationId);
            owner.Parameters.AddWithValue("user", userId);

            var value = await owner.ExecuteScalarAsync(ct);

            if (value is null)
                return null;

            orderId = value is DBNull ? null : (int)value;
        }

        var turns = await ReadTurnsAsync(
            connection, transaction, conversationId, ct);

        await transaction.CommitAsync(ct);

        return new(conversationId, orderId, turns);
    }

    public async Task<ConversationSummary[]> ListAsync(
        string userId,
        CancellationToken ct)
    {
        // Название берём из самого первого сохранённого вопроса.
        // JOIN исключает разговоры без сохранённых сообщений.
        await using var command = dataSource.CreateCommand("""
            SELECT
                c.id,
                COALESCE(first_turn.request_json ->> 'question', ''),
                c.order_id,
                c.updated_at
            FROM support_conversations c
            JOIN LATERAL (
                SELECT request_json
                FROM support_conversation_turns
                WHERE conversation_id=c.id
                ORDER BY turn_id ASC
                LIMIT 1
            ) first_turn ON true
            WHERE c.user_id=@user
            ORDER BY c.updated_at DESC, c.id DESC
            LIMIT @limit
            """);

        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue(
            "limit", ConversationMemory.RecentConversations);

        var conversations = new List<ConversationSummary>();

        await using var reader =
            await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            conversations.Add(new(
                reader.GetGuid(0),
                ConversationMemory.CreateTitle(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetDateTime(3)));
        }

        return conversations.ToArray();
    }

    private static async Task<ConversationTurn[]> ReadTurnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid conversationId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT request_json::text, reply_json::text, created_at
            FROM support_conversation_turns
            WHERE conversation_id=@id
            ORDER BY turn_id DESC
            LIMIT @limit
            """, connection, transaction);

        command.Parameters.AddWithValue("id", conversationId);
        command.Parameters.AddWithValue(
            "limit", ConversationMemory.DisplayTurns);

        var turns = new List<ConversationTurn>();

        await using var reader =
            await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var request = JsonSerializer.Deserialize<SupportRequest>(
                reader.GetString(0), Json)
                ?? throw new InvalidOperationException(
                    "Не удалось прочитать сохранённый запрос.");

            var reply = JsonSerializer.Deserialize<SupportReply>(
                reader.GetString(1), Json)
                ?? throw new InvalidOperationException(
                    "Не удалось прочитать сохранённый ответ.");

            turns.Add(new(
                request,
                reply,
                reader.GetDateTime(2)));
        }

        turns.Reverse();

        return turns.ToArray();
    }

    private sealed class Lease(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SupportRequest request,
        string hash,
        ConversationTurn[] history,
        SupportReply? cached) : IConversationLease
    {
        private bool saved;

        public IReadOnlyList<ConversationTurn> History { get; } =
            history;

        public SupportReply? CachedReply { get; } = cached;

        public async Task SaveAsync(
            SupportReply reply,
            CancellationToken ct)
        {
            if (saved || CachedReply is not null)
                throw new InvalidOperationException(
                    "Запрос уже сохранён.");

            await using var command = new NpgsqlCommand("""
                INSERT INTO support_conversation_turns (
                    conversation_id,
                    request_id,
                    request_hash,
                    request_json,
                    reply_json
                )
                VALUES(
                    @id,
                    @request,
                    @hash,
                    CAST(@input AS jsonb),
                    CAST(@reply AS jsonb)
                );

                UPDATE support_conversations
                SET order_id=@order,
                    updated_at=clock_timestamp()
                WHERE id=@id;
                """, connection, transaction);

            command.Parameters.AddWithValue(
                "id", request.ConversationId!.Value);
            command.Parameters.AddWithValue(
                "request", request.RequestId);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue(
                "input", JsonSerializer.Serialize(request, Json));
            command.Parameters.AddWithValue(
                "reply", JsonSerializer.Serialize(reply, Json));

            command.Parameters.Add("order", NpgsqlDbType.Integer)
                .Value = request.OrderId is int id
                    ? id
                    : DBNull.Value;

            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);

            saved = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}