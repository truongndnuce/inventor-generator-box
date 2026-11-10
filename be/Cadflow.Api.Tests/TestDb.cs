using Npgsql;

namespace Cadflow.Api.Tests;

/// <summary>
/// Một database PostgreSQL riêng cho mỗi test (cadflow_test_xxxx), xóa khi xong, nên các test không dẫm lên nhau.
/// Máy chủ lấy từ biến môi trường CADFLOW_TEST_PG (mặc định "Host=localhost;Username=admin").
/// </summary>
public sealed class TestDb : IDisposable
{
    private static readonly string Server = Environment.GetEnvironmentVariable("CADFLOW_TEST_PG") ?? "Host=localhost;Username=admin";
    private readonly string _name = "cadflow_test_" + Guid.NewGuid().ToString("N")[..10];

    public string ConnectionString => $"{Server};Database={_name}";
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "cadflow-test-" + Guid.NewGuid().ToString("N")[..10]);

    public TestDb()
    {
        Directory.CreateDirectory(DataDir);
        try { Admin($"CREATE DATABASE {_name}"); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Cannot reach PostgreSQL with '{Server}'. Start it or set CADFLOW_TEST_PG. {ex.Message}", ex);
        }
    }

    public JobStore NewStore() => new(DataDir, ConnectionString);

    /// <summary>Chạy SQL thẳng vào DB test (để dựng tình huống như heartbeat quá hạn).</summary>
    public void Sql(string sql)
    {
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        try { Admin($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)"); } catch { /* bỏ qua */ }
        try { Directory.Delete(DataDir, true); } catch { /* bỏ qua */ }
    }

    private static void Admin(string sql)
    {
        using var conn = new NpgsqlConnection(Server + ";Database=postgres");
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}
