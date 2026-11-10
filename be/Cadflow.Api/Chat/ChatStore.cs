using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Cadflow.Api.Chat;

public sealed record ChatSummary(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
/// <summary>One row of a conversation: role = user | assistant | proposal | job; data holds the rest of the card (proposal/job).</summary>
public sealed record ChatItem(string Role, string? Text, JsonElement? Data);
public sealed record ChatDetail(ChatSummary Chat, List<ChatItem> Items);

/// <summary>
/// Chat history on PostgreSQL (tables chat_conversations, chat_messages). A conversation belongs to an "owner" (the user name,
/// like requestedBy of a job; the system has no login). Every query filters by owner.
/// </summary>
public sealed class ChatStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS chat_conversations (
            id text PRIMARY KEY,
            owner text NOT NULL DEFAULT '',
            title text NOT NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_chat_conversations_owner ON chat_conversations (owner, updated_at DESC);
        CREATE TABLE IF NOT EXISTS chat_messages (
            id bigserial PRIMARY KEY,
            conversation_id text NOT NULL REFERENCES chat_conversations (id) ON DELETE CASCADE,
            role text NOT NULL,
            text text,
            data jsonb,
            created_at timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_chat_messages_conv ON chat_messages (conversation_id, id);
        CREATE INDEX IF NOT EXISTS ix_chat_messages_draft ON chat_messages ((data ->> 'draftId')) WHERE data ? 'draftId';
        """;

    private readonly NpgsqlDataSource _db;

    public ChatStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand(Schema, conn);
        cmd.ExecuteNonQuery();
    }

    public static string TitleOf(string firstMessage)
    {
        var t = string.Join(' ', firstMessage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length == 0 ? "New chat" : t.Length > 40 ? t[..40] + "…" : t;
    }

    public async Task<ChatSummary> CreateAsync(string owner, string title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N")[..12];
        await using var cmd = _db.CreateCommand("INSERT INTO chat_conversations (id, owner, title, created_at, updated_at) VALUES (@id, @o, @t, @n, @n)");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("n", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return new ChatSummary(id, title, now, now);
    }

    public async Task<List<ChatSummary>> ListAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT id, title, created_at, updated_at FROM chat_conversations WHERE owner = @o ORDER BY updated_at DESC LIMIT 500");
        cmd.Parameters.AddWithValue("o", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChatSummary>();
        while (await r.ReadAsync(ct)) list.Add(new ChatSummary(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    public async Task<ChatDetail?> GetAsync(string id, string owner, CancellationToken ct = default)
    {
        ChatSummary? chat;
        await using (var cmd = _db.CreateCommand("SELECT id, title, created_at, updated_at FROM chat_conversations WHERE id = @id AND owner = @o"))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("o", owner);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            chat = await r.ReadAsync(ct) ? new ChatSummary(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)) : null;
        }
        if (chat == null) return null;

        await using var mc = _db.CreateCommand("SELECT role, text, data::text FROM chat_messages WHERE conversation_id = @id ORDER BY id");
        mc.Parameters.AddWithValue("id", id);
        await using var mr = await mc.ExecuteReaderAsync(ct);
        var items = new List<ChatItem>();
        while (await mr.ReadAsync(ct))
            items.Add(new ChatItem(mr.GetString(0), mr.IsDBNull(1) ? null : mr.GetString(1), mr.IsDBNull(2) ? null : JsonDocument.Parse(mr.GetString(2)).RootElement.Clone()));
        return new ChatDetail(chat, items);
    }

    public async Task<bool> ExistsAsync(string id, string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT 1 FROM chat_conversations WHERE id = @id AND owner = @o");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    public async Task<bool> RenameAsync(string id, string owner, string title, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE chat_conversations SET title = @t WHERE id = @id AND owner = @o");
        cmd.Parameters.AddWithValue("t", title);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(string id, string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM chat_conversations WHERE id = @id AND owner = @o");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("o", owner);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task AddAsync(string conversationId, string role, string? text, object? data = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("INSERT INTO chat_messages (conversation_id, role, text, data, created_at) VALUES (@c, @r, @t, @d, @n)", conn, tx))
        {
            cmd.Parameters.AddWithValue("c", conversationId);
            cmd.Parameters.AddWithValue("r", role);
            cmd.Parameters.AddWithValue("t", (object?)text ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = data == null ? DBNull.Value : JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            cmd.Parameters.AddWithValue("n", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var up = new NpgsqlCommand("UPDATE chat_conversations SET updated_at = @n WHERE id = @c", conn, tx))
        {
            up.Parameters.AddWithValue("n", now);
            up.Parameters.AddWithValue("c", conversationId);
            await up.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Marks a proposal card as turned into a job (so reopening the conversation still shows "created").</summary>
    public async Task MarkProposalCreatedAsync(string draftId, string jobId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE chat_messages SET data = data || jsonb_build_object('jobId', @j::text) WHERE data ->> 'draftId' = @d");
        cmd.Parameters.AddWithValue("j", jobId);
        cmd.Parameters.AddWithValue("d", draftId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
