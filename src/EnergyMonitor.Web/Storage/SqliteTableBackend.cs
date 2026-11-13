using Microsoft.Data.Sqlite;

namespace EnergyMonitor.Web.Storage;

/// <summary>
/// The table operations in one SQLite file, for running on a single private machine. Every logical table
/// shares one SQL table keyed by (table, partition, row). The default BINARY collation orders keys the
/// same way as <see cref="string.CompareOrdinal(string, string)"/> for the ASCII keys the stores write.
/// </summary>
public sealed class SqliteTableBackend : ITableBackend
{
    private readonly string _connectionString;
    private readonly Lazy<Task> _ready;

    public SqliteTableBackend(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
        }.ToString();
        _ready = new Lazy<Task>(CreateSchemaAsync);
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // WAL lets the dashboard read while a sync writes; NORMAL sync is safe with WAL and much faster.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS rows (
                tbl  TEXT NOT NULL,
                pk   TEXT NOT NULL,
                rk   TEXT NOT NULL,
                json TEXT NOT NULL,
                tag  TEXT NULL,
                PRIMARY KEY (tbl, pk, rk)
            ) WITHOUT ROWID;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await _ready.Value;
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 30000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private static Row Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));

    private static async Task<IReadOnlyList<Row>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<Row?> GetAsync(string table, string pk, string rk, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pk, rk, json, tag FROM rows WHERE tbl = $t AND pk = $pk AND rk = $rk";
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$pk", pk);
        command.Parameters.AddWithValue("$rk", rk);
        return (await ReadAllAsync(command, ct)).FirstOrDefault();
    }

    public async Task UpsertAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rows (tbl, pk, rk, json, tag) VALUES ($t, $pk, $rk, $json, $tag)
            ON CONFLICT (tbl, pk, rk) DO UPDATE SET json = excluded.json, tag = excluded.tag
            """;
        var t = command.Parameters.Add("$t", SqliteType.Text);
        var pk = command.Parameters.Add("$pk", SqliteType.Text);
        var rk = command.Parameters.Add("$rk", SqliteType.Text);
        var json = command.Parameters.Add("$json", SqliteType.Text);
        var tag = command.Parameters.Add("$tag", SqliteType.Text);
        foreach (var row in rows)
        {
            t.Value = table;
            pk.Value = row.Pk;
            rk.Value = row.Rk;
            json.Value = row.Json;
            tag.Value = (object?)row.Tag ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> TryAddAsync(string table, Row row, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO rows (tbl, pk, rk, json, tag) VALUES ($t, $pk, $rk, $json, $tag) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$pk", row.Pk);
        command.Parameters.AddWithValue("$rk", row.Rk);
        command.Parameters.AddWithValue("$json", row.Json);
        command.Parameters.AddWithValue("$tag", (object?)row.Tag ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task DeleteAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM rows WHERE tbl = $t AND pk = $pk AND rk = $rk";
        var t = command.Parameters.Add("$t", SqliteType.Text);
        var pk = command.Parameters.Add("$pk", SqliteType.Text);
        var rk = command.Parameters.Add("$rk", SqliteType.Text);
        foreach (var row in rows)
        {
            t.Value = table;
            pk.Value = row.Pk;
            rk.Value = row.Rk;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Row>> QueryAsync(string table, string pk, string? rkFrom, string? rkTo, int? take, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pk, rk, json, tag FROM rows
            WHERE tbl = $t AND pk = $pk AND ($from IS NULL OR rk >= $from) AND ($to IS NULL OR rk < $to)
            ORDER BY rk LIMIT $take
            """;
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$pk", pk);
        command.Parameters.AddWithValue("$from", (object?)rkFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)rkTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$take", take ?? -1);
        return await ReadAllAsync(command, ct);
    }

    public async Task<IReadOnlyList<Row>> QueryPartitionsAsync(string table, string pkFrom, string pkTo, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pk, rk, json, tag FROM rows WHERE tbl = $t AND pk >= $from AND pk < $to ORDER BY pk, rk";
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$from", pkFrom);
        command.Parameters.AddWithValue("$to", pkTo);
        return await ReadAllAsync(command, ct);
    }

    /// <summary>True when no table holds a row; the one-time copy refuses to write into anything else.</summary>
    public async Task<bool> IsEmptyAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM rows)";
        return (long)(await command.ExecuteScalarAsync(ct))! == 0;
    }
}

/// <summary>Copies stored tables from one backend to a new SQLite file, e.g. from Azure Table Storage when moving off Azure.</summary>
public static class TableCopy
{
    // Partition keys are text; U+FFFF sorts after every character the stores use.
    private const string Last = "￿";

    public static async Task<Dictionary<string, int>> CopyAsync(ITableBackend source, SqliteTableBackend target, IReadOnlyCollection<string> tables, CancellationToken ct)
    {
        if (!await target.IsEmptyAsync(ct))
        {
            throw new InvalidOperationException("The target database already holds data. Copy into a new, empty file.");
        }

        var counts = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            var rows = await source.QueryPartitionsAsync(table, "", Last, ct);
            await target.UpsertAsync(table, rows, ct);
            counts[table] = rows.Count;
        }

        return counts;
    }
}
