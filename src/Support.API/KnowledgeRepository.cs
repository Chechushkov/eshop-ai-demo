using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace eShop.SupportApi;

public sealed class KnowledgeRepository(NpgsqlDataSource dataSource, OpenAiApi openAi,
    OpenAiSettings settings) : IKnowledgeSearch, ITicketStore
{
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE IF NOT EXISTS knowledge_documents (
                id text PRIMARY KEY, title text NOT NULL, source_uri text,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            ALTER TABLE knowledge_documents ADD COLUMN IF NOT EXISTS content_hash text;
            CREATE TABLE IF NOT EXISTS knowledge_chunks (
                id bigserial PRIMARY KEY,
                document_id text NOT NULL REFERENCES knowledge_documents(id) ON DELETE CASCADE,
                chunk_number integer NOT NULL CHECK (chunk_number >= 0), content text NOT NULL,
                embedding vector(1536),
                embedding_model text NOT NULL DEFAULT 'text-embedding-3-small',
                UNIQUE(document_id, chunk_number)
            );
            CREATE INDEX IF NOT EXISTS knowledge_chunks_embedding_hnsw
                ON knowledge_chunks USING hnsw (embedding vector_cosine_ops);
            CREATE TABLE IF NOT EXISTS support_tickets (
                id text PRIMARY KEY, user_id text NOT NULL, order_id integer NOT NULL,
                request_id uuid NOT NULL, summary text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                UNIQUE(user_id, order_id, request_id)
            );
            """);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ImportAsync(string directory, CancellationToken ct)
    {
        var files = Directory.GetFiles(directory, "*.md").Order().ToArray();
        if (files.Length == 0) throw new InvalidOperationException("The Knowledge directory contains no policy documents.");
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
            string id = "support-" + Path.GetFileNameWithoutExtension(file);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            var chunks = Split(text);
            if (chunks.Length == 0) throw new InvalidOperationException("Empty knowledge document: " + id);
            await using var check = dataSource.CreateCommand("""
                SELECT d.content_hash = @hash
                  AND (SELECT count(*) FROM knowledge_chunks c WHERE c.document_id=d.id
                       AND c.embedding IS NOT NULL AND c.embedding_model=@model) = @count
                FROM knowledge_documents d WHERE d.id=@id
                """);
            check.Parameters.AddWithValue("hash", hash);
            check.Parameters.AddWithValue("model", settings.EmbeddingModel);
            check.Parameters.AddWithValue("count", chunks.Length);
            check.Parameters.AddWithValue("id", id);
            if (await check.ExecuteScalarAsync(ct) is true)
            {
                Console.WriteLine($"[rag] Cached: {id}");
                continue;
            }
            // Generate embeddings before atomically replacing this document.
            var vectors = await openAi.EmbedAsync(chunks, ct);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            string title = text.Split('\n').First().Trim().TrimStart('#').Trim();
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO knowledge_documents(id,title,source_uri,content_hash)
                    VALUES(@id,@title,@uri,@hash)
                ON CONFLICT(id) DO UPDATE SET title=EXCLUDED.title, source_uri=EXCLUDED.source_uri,
                    content_hash=EXCLUDED.content_hash, updated_at=now();
                DELETE FROM knowledge_chunks WHERE document_id=@id;
                """, connection, transaction);
            upsert.Parameters.AddWithValue("id", id);
            upsert.Parameters.AddWithValue("title", title);
            upsert.Parameters.AddWithValue("uri", "Knowledge/" + Path.GetFileName(file));
            upsert.Parameters.AddWithValue("hash", hash);
            await upsert.ExecuteNonQueryAsync(ct);
            for (int i = 0; i < chunks.Length; i++)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO knowledge_chunks(document_id,chunk_number,content,embedding,embedding_model)
                        VALUES(@id,@number,@text,CAST(@vector AS vector),@model)
                    """, connection, transaction);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("number", i);
                insert.Parameters.AddWithValue("text", chunks[i]);
                insert.Parameters.AddWithValue("vector", VectorText(vectors[i]));
                insert.Parameters.AddWithValue("model", settings.EmbeddingModel);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            Console.WriteLine($"[rag] Imported: {id}, chunks: {chunks.Length}");
        }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
    {
        var vector = (await openAi.EmbedAsync([query], ct))[0];
        await using var command = dataSource.CreateCommand("""
            SELECT d.id || '#' || lpad((c.chunk_number+1)::text,3,'0'), d.title, c.content,
                   1 - (c.embedding <=> CAST(@vector AS vector)) AS score
            FROM knowledge_chunks c JOIN knowledge_documents d ON d.id=c.document_id
            WHERE c.embedding IS NOT NULL AND c.embedding_model=@model
                AND d.source_uri LIKE 'Knowledge/%'
            ORDER BY c.embedding <=> CAST(@vector AS vector)
            LIMIT 3
            """);
        command.Parameters.AddWithValue("vector", VectorText(vector));
        command.Parameters.AddWithValue("model", settings.EmbeddingModel);
        var result = new List<KnowledgeHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            double score = reader.GetDouble(3);
            if (score >= 0.30)
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), score));
        }
        Console.WriteLine("[rag] Matches: " + string.Join(", ", result.Select(x => $"{x.Id} ({x.Score:F3})")));
        return result;
    }

    public async Task<SupportTicket> CreateAsync(string userId, int orderId, Guid requestId,
        string summary, CancellationToken ct)
    {
        // The unique key prevents duplicate tickets on tool retries and HTTP replays.
        await using var command = dataSource.CreateCommand("""
            INSERT INTO support_tickets(id,user_id,order_id,request_id,summary)
                VALUES(@id,@user,@order,@request,@summary)
            ON CONFLICT(user_id,order_id,request_id) DO UPDATE SET request_id=EXCLUDED.request_id
            RETURNING id,order_id,summary,created_at
            """);
        command.Parameters.AddWithValue("id", "T-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("summary", summary);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The support ticket was not saved.");
        return new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3));
    }

    public static string VectorText(float[] vector) =>
        "[" + string.Join(",", vector.Select(x => x.ToString("G9", CultureInfo.InvariantCulture))) + "]";

    public static string[] Split(string text)
    {
        // Short demo policies use 900-character chunks with a 100-character overlap.
        text = text.Replace("\r\n", "\n").Trim();
        var chunks = new List<string>();
        for (int start = 0; start < text.Length; start += 800)
        {
            chunks.Add(text.Substring(start, Math.Min(900, text.Length - start)));
            if (start + 900 >= text.Length) break;
        }
        return chunks.ToArray();
    }
}
