using System.Text.Json.Serialization;
using Npgsql;

namespace Cadflow.Api.Vault;

public sealed record VaultFolder(string Id, string? ParentId, string Name, string? Kind, string? JobId, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>An item as the file list shows it: the item plus its current version.</summary>
public sealed record VaultItem(string Id, string FolderId, string Number, string Name, string Kind, int Version, string State, long Size, string ContentType,
    string CreatedBy, DateTimeOffset CreatedAt, string ModifiedBy, DateTimeOffset ModifiedAt, string? JobId);

public sealed record VaultVersion(int Version, long Size, string ContentType, string? Comment, string CreatedBy, DateTimeOffset CreatedAt, string? SourceJobId,
    [property: JsonIgnore] string? R2Key);

/// <summary>Where the bytes of one version live (R2 key, or null = server disk), so the caller can delete them.</summary>
public sealed record VaultFileRef(string ItemId, int Version, string Name, string? R2Key);

public sealed record PutResult(VaultItem Item, bool Created, bool Unchanged);

public sealed class VaultException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The document vault (PLM-style) on PostgreSQL: folders, items (a file with a number, state and current version) and versions.
/// Only metadata lives here; the bytes are handled by <see cref="VaultFiles"/>. No login: "who" is the free-text name sent by the client.
/// </summary>
public sealed class VaultStore
{
    public const string InWork = "In Work", UnderReview = "Under Review", Approved = "Approved", Released = "Released";
    public static readonly string[] States = [InWork, UnderReview, Approved, Released];
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [InWork] = [UnderReview],
        [UnderReview] = [InWork, Approved],
        [Approved] = [InWork, Released],
        [Released] = [],
    };

    /// <summary>The three fixed sub-folders of a job folder, in display order.</summary>
    public static readonly string[] JobSubfolders = ["image", "drawing", "model"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vault_folders (
            id text PRIMARY KEY,
            parent_id text REFERENCES vault_folders (id) ON DELETE CASCADE,
            name text NOT NULL,
            kind text,
            job_id text,
            created_by text NOT NULL DEFAULT '',
            created_at timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_vault_folders_name ON vault_folders ((COALESCE(parent_id, '')), lower(name));
        CREATE INDEX IF NOT EXISTS ix_vault_folders_job ON vault_folders (job_id) WHERE job_id IS NOT NULL;
        CREATE SEQUENCE IF NOT EXISTS vault_item_seq;
        CREATE TABLE IF NOT EXISTS vault_items (
            id text PRIMARY KEY,
            folder_id text NOT NULL REFERENCES vault_folders (id) ON DELETE CASCADE,
            number text NOT NULL UNIQUE,
            name text NOT NULL,
            kind text NOT NULL,
            current_version int NOT NULL DEFAULT 1,
            state text NOT NULL DEFAULT 'In Work',
            created_by text NOT NULL DEFAULT '',
            created_at timestamptz NOT NULL,
            modified_by text NOT NULL DEFAULT '',
            modified_at timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_vault_items_name ON vault_items (folder_id, lower(name));
        CREATE INDEX IF NOT EXISTS ix_vault_items_modified ON vault_items (modified_at DESC);
        CREATE TABLE IF NOT EXISTS vault_versions (
            item_id text NOT NULL REFERENCES vault_items (id) ON DELETE CASCADE,
            version int NOT NULL,
            r2_key text,
            size bigint NOT NULL,
            content_type text NOT NULL,
            comment text,
            source_job_id text,
            created_by text NOT NULL DEFAULT '',
            created_at timestamptz NOT NULL,
            PRIMARY KEY (item_id, version)
        );
        """;

    private const string ItemCols = """
        i.id, i.folder_id, i.number, i.name, i.kind, i.current_version, i.state, v.size, v.content_type,
        i.created_by, i.created_at, i.modified_by, i.modified_at, v.source_job_id
        """;
    private const string ItemFrom = "vault_items i JOIN vault_versions v ON v.item_id = i.id AND v.version = i.current_version";

    private readonly NpgsqlDataSource _db;

    public VaultStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "SELECT pg_advisory_xact_lock(727002)");
        Exec(conn, tx, Schema);
        tx.Commit();
    }

    // ---------------------------------------------------------------- helpers

    private static void Exec(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params (string, object?)[] args)
    {
        using var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params (string, object?)[] args)
    {
        var cmd = new NpgsqlCommand(sql, c, t);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static VaultFolder ReadFolder(NpgsqlDataReader r) =>
        new(r.GetString(0), Str(r, 1), r.GetString(2), Str(r, 3), Str(r, 4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6));

    private static VaultItem ReadItem(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetInt64(7), r.GetString(8),
            r.GetString(9), r.GetFieldValue<DateTimeOffset>(10), r.GetString(11), r.GetFieldValue<DateTimeOffset>(12), Str(r, 13));

    private const string FolderCols = "id, parent_id, name, kind, job_id, created_by, created_at";

    /// <summary>A name that is safe as a file or folder name in the vault (no path separators, no control characters).</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 150 && name == name.Trim() && name is not "." and not ".."
        && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0 && !name.Any(char.IsControl);

    /// <summary>The kind of a file by its extension: image, drawing, model or other. It decides the item number prefix.</summary>
    public static string KindOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" => "image",
        ".pdf" or ".dwg" or ".dxf" or ".idw" => "drawing",
        ".ipt" or ".iam" or ".stp" or ".step" or ".rvt" or ".rfa" or ".ifc" or ".igs" or ".iges" or ".sat" => "model",
        _ => "other",
    };

    private static string PrefixOf(string kind) => kind switch { "image" => "IMG", "drawing" => "DRW", "model" => "MDL", _ => "DOC" };

    // ---------------------------------------------------------------- folders

    public List<VaultFolder> Folders()
    {
        using var conn = _db.OpenConnection();
        using var cmd = Cmd(conn, null, $"SELECT {FolderCols} FROM vault_folders ORDER BY created_at, name");
        using var r = cmd.ExecuteReader();
        var list = new List<VaultFolder>();
        while (r.Read()) list.Add(ReadFolder(r));
        return list;
    }

    public VaultFolder? Folder(string id)
    {
        using var conn = _db.OpenConnection();
        return Folder(conn, null, id);
    }

    private static VaultFolder? Folder(NpgsqlConnection conn, NpgsqlTransaction? tx, string id)
    {
        using var cmd = Cmd(conn, tx, $"SELECT {FolderCols} FROM vault_folders WHERE id = @id", ("id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFolder(r) : null;
    }

    /// <summary>The path from the root to the folder, inclusive.</summary>
    public List<VaultFolder> Breadcrumb(string id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = Cmd(conn, null, $"""
            WITH RECURSIVE up AS (
                SELECT {FolderCols}, 0 AS depth FROM vault_folders WHERE id = @id
                UNION ALL
                SELECT f.id, f.parent_id, f.name, f.kind, f.job_id, f.created_by, f.created_at, up.depth + 1
                FROM vault_folders f JOIN up ON up.parent_id = f.id)
            SELECT {FolderCols} FROM up ORDER BY depth DESC
            """, ("id", id));
        using var r = cmd.ExecuteReader();
        var list = new List<VaultFolder>();
        while (r.Read()) list.Add(ReadFolder(r));
        return list;
    }

    public VaultFolder CreateFolder(string? parentId, string name, string? createdBy, string? kind = null, string? jobId = null)
    {
        if (!IsValidName(name)) throw new VaultException(400, "Folder name is empty, too long or contains / \\ : * ? \" < > |.");
        using var conn = _db.OpenConnection();
        if (parentId != null && Folder(conn, null, parentId) == null) throw new VaultException(404, "Parent folder not found.");
        return Insert(conn, null, parentId, name, createdBy, kind, jobId);
    }

    private static VaultFolder Insert(NpgsqlConnection conn, NpgsqlTransaction? tx, string? parentId, string name, string? by, string? kind, string? jobId)
    {
        var f = new VaultFolder(NewId(), parentId, name, kind, jobId, Who(by), DateTimeOffset.UtcNow);
        try
        {
            Exec(conn, tx, "INSERT INTO vault_folders (id, parent_id, name, kind, job_id, created_by, created_at) VALUES (@id, @p, @n, @k, @j, @c, @t)",
                ("id", f.Id), ("p", parentId), ("n", name), ("k", kind), ("j", jobId), ("c", f.CreatedBy), ("t", f.CreatedAt));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new VaultException(409, $"A folder named '{name}' already exists here.");
        }
        return f;
    }

    /// <summary>The folder <c>&lt;job name&gt;</c> of a job with its image/drawing/model sub-folders (created when missing). Returns the sub-folder ids by name.</summary>
    public (VaultFolder Job, Dictionary<string, VaultFolder> Sub) EnsureJobFolder(string jobId, string name, string? by)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        Exec(conn, tx, "SELECT pg_advisory_xact_lock(727003)");
        VaultFolder? job;
        using (var cmd = Cmd(conn, tx, $"SELECT {FolderCols} FROM vault_folders WHERE job_id = @j AND parent_id IS NULL", ("j", jobId)))
        using (var r = cmd.ExecuteReader()) job = r.Read() ? ReadFolder(r) : null;
        job ??= Insert(conn, tx, null, name, by, null, jobId);

        var sub = new Dictionary<string, VaultFolder>();
        foreach (var s in JobSubfolders)
        {
            VaultFolder? f;
            using (var cmd = Cmd(conn, tx, $"SELECT {FolderCols} FROM vault_folders WHERE parent_id = @p AND kind = @k", ("p", job.Id), ("k", s)))
            using (var r = cmd.ExecuteReader()) f = r.Read() ? ReadFolder(r) : null;
            sub[s] = f ?? Insert(conn, tx, job.Id, s, by, s, null);
        }
        tx.Commit();
        return (job, sub);
    }

    public VaultFolder RenameFolder(string id, string name)
    {
        if (!IsValidName(name)) throw new VaultException(400, "Folder name is empty, too long or contains / \\ : * ? \" < > |.");
        using var conn = _db.OpenConnection();
        var f = Folder(conn, null, id) ?? throw new VaultException(404, "Folder not found.");
        if (f.Kind != null) throw new VaultException(409, $"The '{f.Kind}' folder is fixed and cannot be renamed.");
        try { Exec(conn, null, "UPDATE vault_folders SET name = @n WHERE id = @id", ("n", name), ("id", id)); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new VaultException(409, $"A folder named '{name}' already exists here.");
        }
        return f with { Name = name };
    }

    /// <summary>Deletes the folder with everything below it. Returns the files that were stored for it so the caller can remove the bytes.</summary>
    public List<VaultFileRef> DeleteFolder(string id)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var f = Folder(conn, tx, id) ?? throw new VaultException(404, "Folder not found.");
        if (f.Kind != null) throw new VaultException(409, $"The '{f.Kind}' folder is fixed and cannot be deleted.");
        var refs = new List<VaultFileRef>();
        using (var cmd = Cmd(conn, tx, """
            WITH RECURSIVE down AS (
                SELECT id FROM vault_folders WHERE id = @id
                UNION ALL
                SELECT f.id FROM vault_folders f JOIN down ON f.parent_id = down.id)
            SELECT v.item_id, v.version, i.name, v.r2_key
            FROM vault_versions v JOIN vault_items i ON i.id = v.item_id
            WHERE i.folder_id IN (SELECT id FROM down)
            """, ("id", id)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) refs.Add(new VaultFileRef(r.GetString(0), r.GetInt32(1), r.GetString(2), Str(r, 3)));
        Exec(conn, tx, "DELETE FROM vault_folders WHERE id = @id", ("id", id));   // items and versions go with it (ON DELETE CASCADE)
        tx.Commit();
        return refs;
    }

    // ---------------------------------------------------------------- items

    public List<VaultItem> Items(string folderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = Cmd(conn, null, $"SELECT {ItemCols} FROM {ItemFrom} WHERE i.folder_id = @f ORDER BY lower(i.name)", ("f", folderId));
        using var r = cmd.ExecuteReader();
        var list = new List<VaultItem>();
        while (r.Read()) list.Add(ReadItem(r));
        return list;
    }

    public VaultItem? Item(string id)
    {
        using var conn = _db.OpenConnection();
        return Item(conn, null, id);
    }

    private static VaultItem? Item(NpgsqlConnection conn, NpgsqlTransaction? tx, string id)
    {
        using var cmd = Cmd(conn, tx, $"SELECT {ItemCols} FROM {ItemFrom} WHERE i.id = @id", ("id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    public List<VaultVersion> Versions(string itemId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = Cmd(conn, null,
            "SELECT version, size, content_type, comment, created_by, created_at, source_job_id, r2_key FROM vault_versions WHERE item_id = @i ORDER BY version DESC", ("i", itemId));
        using var r = cmd.ExecuteReader();
        var list = new List<VaultVersion>();
        while (r.Read())
            list.Add(new VaultVersion(r.GetInt32(0), r.GetInt64(1), r.GetString(2), Str(r, 3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), Str(r, 6), Str(r, 7)));
        return list;
    }

    /// <summary>Search across all folders by number or name, optionally by kind and state. Newest first, at most 200.</summary>
    public List<VaultItem> Search(string? q, string? kind, string? state)
    {
        using var conn = _db.OpenConnection();
        using var cmd = Cmd(conn, null, $"""
            SELECT {ItemCols} FROM {ItemFrom}
            WHERE (@q::text IS NULL OR i.name ILIKE @like OR i.number ILIKE @like)
              AND (@k::text IS NULL OR i.kind = @k) AND (@s::text IS NULL OR i.state = @s)
            ORDER BY i.modified_at DESC LIMIT 200
            """,
            ("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim()),
            ("like", "%" + (q ?? "").Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"),
            ("k", string.IsNullOrWhiteSpace(kind) ? null : kind),
            ("s", string.IsNullOrWhiteSpace(state) ? null : state));
        using var r = cmd.ExecuteReader();
        var list = new List<VaultItem>();
        while (r.Read()) list.Add(ReadItem(r));
        return list;
    }

    /// <summary>
    /// Adds a file to a folder: a new item (version 1), or a new version when the folder already has an item of that name (it goes back to In Work).
    /// <paramref name="place"/> stores the bytes for (itemId, version) and returns where they went; it runs inside the transaction, so a failure leaves nothing behind.
    /// With a <paramref name="sourceJobId"/> already recorded on the current version nothing is added (saving the same job twice is a no-op).
    /// </summary>
    public PutResult Put(string folderId, string name, string? by, string? comment, string contentType, string? sourceJobId,
        Func<string, int, (string? R2Key, long Size)> place)
    {
        if (!IsValidName(name)) throw new VaultException(400, "File name is empty, too long or contains / \\ : * ? \" < > |.");
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        if (Folder(conn, tx, folderId) == null) throw new VaultException(404, "Folder not found.");
        var who = Who(by);
        var now = DateTimeOffset.UtcNow;

        string? existingId = null; int current = 0; string? currentJob = null;
        using (var cmd = Cmd(conn, tx, """
            SELECT i.id, i.current_version, v.source_job_id FROM vault_items i
            JOIN vault_versions v ON v.item_id = i.id AND v.version = i.current_version
            WHERE i.folder_id = @f AND lower(i.name) = lower(@n) FOR UPDATE OF i
            """, ("f", folderId), ("n", name)))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { existingId = r.GetString(0); current = r.GetInt32(1); currentJob = Str(r, 2); }

        if (existingId != null && sourceJobId != null && currentJob == sourceJobId)
            return new PutResult(Item(conn, tx, existingId)!, false, true);

        var created = existingId == null;
        var id = existingId ?? NewId();
        var version = current + 1;
        var kind = KindOf(name);
        try
        {
            if (created)
            {
                long seq;
                using (var cmd = Cmd(conn, tx, "SELECT nextval('vault_item_seq')")) seq = (long)cmd.ExecuteScalar()!;
                Exec(conn, tx, """
                    INSERT INTO vault_items (id, folder_id, number, name, kind, current_version, state, created_by, created_at, modified_by, modified_at)
                    VALUES (@id, @f, @num, @n, @k, 1, 'In Work', @by, @t, @by, @t)
                    """, ("id", id), ("f", folderId), ("num", $"{PrefixOf(kind)}-{seq:D6}"), ("n", name), ("k", kind), ("by", who), ("t", now));
            }
            var (r2Key, size) = place(id, version);
            Exec(conn, tx, """
                INSERT INTO vault_versions (item_id, version, r2_key, size, content_type, comment, source_job_id, created_by, created_at)
                VALUES (@id, @v, @r2, @sz, @ct, @c, @j, @by, @t)
                """, ("id", id), ("v", version), ("r2", r2Key), ("sz", size), ("ct", contentType), ("c", string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()),
                ("j", sourceJobId), ("by", who), ("t", now));
            if (!created)
                Exec(conn, tx, "UPDATE vault_items SET current_version = @v, state = 'In Work', modified_by = @by, modified_at = @t WHERE id = @id",
                    ("v", version), ("by", who), ("t", now), ("id", id));
            tx.Commit();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new VaultException(409, $"'{name}' was added at the same time by someone else. Try again.");
        }
        return new PutResult(Item(id)!, created, false);
    }

    /// <summary>A new version of an existing item (check-in). The file name stays the item's name.</summary>
    public PutResult AddVersion(string itemId, string? by, string? comment, string contentType, Func<string, int, (string? R2Key, long Size)> place)
    {
        var item = Item(itemId) ?? throw new VaultException(404, "Item not found.");
        return Put(item.FolderId, item.Name, by, comment, contentType, null, place);
    }

    public VaultItem SetState(string id, string state, string? by)
    {
        if (!States.Contains(state)) throw new VaultException(400, $"Unknown state '{state}'. Use one of: {string.Join(", ", States)}.");
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string current;
        using (var cmd = Cmd(conn, tx, "SELECT state FROM vault_items WHERE id = @id FOR UPDATE", ("id", id)))
            current = cmd.ExecuteScalar() as string ?? throw new VaultException(404, "Item not found.");
        if (current == state) return Item(conn, tx, id)!;
        if (!Transitions[current].Contains(state))
            throw new VaultException(409, $"An item in '{current}' cannot go to '{state}'. Allowed: {(Transitions[current].Length == 0 ? "none (check in a new version)" : string.Join(", ", Transitions[current]))}.");
        Exec(conn, tx, "UPDATE vault_items SET state = @s, modified_by = @by, modified_at = @t WHERE id = @id",
            ("s", state), ("by", Who(by)), ("t", DateTimeOffset.UtcNow), ("id", id));
        tx.Commit();
        return Item(id)!;
    }

    /// <summary>Deletes an item with all its versions; returns the stored files so the caller can remove the bytes.</summary>
    public List<VaultFileRef> DeleteItem(string id)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        var refs = new List<VaultFileRef>();
        using (var cmd = Cmd(conn, tx, "SELECT v.item_id, v.version, i.name, v.r2_key FROM vault_versions v JOIN vault_items i ON i.id = v.item_id WHERE v.item_id = @id", ("id", id)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) refs.Add(new VaultFileRef(r.GetString(0), r.GetInt32(1), r.GetString(2), Str(r, 3)));
        if (refs.Count == 0) throw new VaultException(404, "Item not found.");
        Exec(conn, tx, "DELETE FROM vault_items WHERE id = @id", ("id", id));
        tx.Commit();
        return refs;
    }

    /// <summary>Folder id and name of an item's version (for download), or null.</summary>
    public (VaultItem Item, VaultVersion Version)? Version(string itemId, int version)
    {
        var item = Item(itemId);
        var v = item == null ? null : Versions(itemId).FirstOrDefault(x => x.Version == version);
        return item == null || v == null ? null : (item, v);
    }

    private static string Who(string? by)
    {
        var s = by?.Trim() ?? "";
        return s.Length == 0 ? "anonymous" : s.Length > 80 ? s[..80] : s;
    }
}
