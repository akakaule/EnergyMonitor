using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace EnergyMonitor.Web.Storage;

/// <summary>One stored record: keys plus the record as JSON, and an optional filterable tag.</summary>
public sealed record Row(string Pk, string Rk, string Json, string? Tag = null);

/// <summary>
/// The few table operations the stores need. Azure Table Storage in production and against Azurite;
/// an in-memory implementation for tests.
/// </summary>
public interface ITableBackend
{
    Task<Row?> GetAsync(string table, string pk, string rk, CancellationToken ct);

    /// <summary>Insert-or-replace. Rows are written in per-partition batches.</summary>
    Task UpsertAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct);

    /// <summary>Inserts the row unless its key exists. Returns false when it does.</summary>
    Task<bool> TryAddAsync(string table, Row row, CancellationToken ct);

    Task DeleteAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct);

    /// <summary>Rows of one partition with <paramref name="rkFrom"/> ≤ RowKey &lt; <paramref name="rkTo"/>, ascending.</summary>
    Task<IReadOnlyList<Row>> QueryAsync(string table, string pk, string? rkFrom, string? rkTo, int? take, CancellationToken ct);

    /// <summary>Rows of every partition with <paramref name="pkFrom"/> ≤ PartitionKey &lt; <paramref name="pkTo"/>.</summary>
    Task<IReadOnlyList<Row>> QueryPartitionsAsync(string table, string pkFrom, string pkTo, CancellationToken ct);
}

public sealed class AzureTableBackend(TableServiceClient service, string prefix = "em") : ITableBackend
{
    /// <summary>Prefix of every table name; tests use their own.</summary>
    public string Prefix { get; } = prefix;

    private const int BatchSize = 100;
    private readonly ConcurrentDictionary<string, Task<TableClient>> _tables = new();

    private Task<TableClient> TableAsync(string table, CancellationToken ct) =>
        _tables.GetOrAdd(table, async name =>
        {
            var client = service.GetTableClient(Prefix + name);
            await client.CreateIfNotExistsAsync(ct);
            return client;
        });

    private static TableEntity ToEntity(Row row)
    {
        var entity = new TableEntity(row.Pk, row.Rk) { ["Json"] = row.Json };
        if (row.Tag is not null)
        {
            entity["Tag"] = row.Tag;
        }

        return entity;
    }

    private static Row ToRow(TableEntity e) => new(e.PartitionKey, e.RowKey, e.GetString("Json") ?? "{}", e.GetString("Tag"));

    public async Task<Row?> GetAsync(string table, string pk, string rk, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct);
        return response.HasValue ? ToRow(response.Value!) : null;
    }

    public async Task UpsertAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        foreach (var partition in rows.GroupBy(r => r.Pk))
        {
            // Last write wins inside a batch too: a key may appear only once per transaction.
            var distinct = partition.GroupBy(r => r.Rk).Select(g => g.Last());
            foreach (var chunk in distinct.Chunk(BatchSize))
            {
                await client.SubmitTransactionAsync(
                    chunk.Select(r => new TableTransactionAction(TableTransactionActionType.UpsertReplace, ToEntity(r))), ct);
            }
        }
    }

    public async Task<bool> TryAddAsync(string table, Row row, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        try
        {
            await client.AddEntityAsync(ToEntity(row), ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        foreach (var partition in rows.GroupBy(r => r.Pk))
        {
            foreach (var chunk in partition.Chunk(BatchSize))
            {
                try
                {
                    await client.SubmitTransactionAsync(
                        chunk.Select(r => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(r.Pk, r.Rk) { ETag = ETag.All })), ct);
                }
                catch (TableTransactionFailedException ex) when (ex.Status == 404)
                {
                    // Something in the batch was already gone: delete what is left one by one.
                    foreach (var r in chunk)
                    {
                        await client.DeleteEntityAsync(r.Pk, r.Rk, ETag.All, ct);
                    }
                }
            }
        }
    }

    public async Task<IReadOnlyList<Row>> QueryAsync(string table, string pk, string? rkFrom, string? rkTo, int? take, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {pk}");
        if (rkFrom is not null)
        {
            filter += " and " + TableClient.CreateQueryFilter($"RowKey ge {rkFrom}");
        }

        if (rkTo is not null)
        {
            filter += " and " + TableClient.CreateQueryFilter($"RowKey lt {rkTo}");
        }

        var rows = new List<Row>();
        await foreach (var e in client.QueryAsync<TableEntity>(filter, maxPerPage: take, cancellationToken: ct))
        {
            rows.Add(ToRow(e));
            if (rows.Count >= take)
            {
                break;
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<Row>> QueryPartitionsAsync(string table, string pkFrom, string pkTo, CancellationToken ct)
    {
        var client = await TableAsync(table, ct);
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {pkFrom} and PartitionKey lt {pkTo}");
        var rows = new List<Row>();
        await foreach (var e in client.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            rows.Add(ToRow(e));
        }

        return rows;
    }
}

public sealed class InMemoryTableBackend : ITableBackend
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SortedDictionary<(string Pk, string Rk), Row>> _tables = [];

    private SortedDictionary<(string Pk, string Rk), Row> Table(string table)
    {
        if (!_tables.TryGetValue(table, out var rows))
        {
            _tables[table] = rows = new SortedDictionary<(string, string), Row>(KeyComparer.Instance);
        }

        return rows;
    }

    public Task<Row?> GetAsync(string table, string pk, string rk, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Table(table).GetValueOrDefault((pk, rk)));
        }
    }

    public Task UpsertAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var row in rows)
            {
                Table(table)[(row.Pk, row.Rk)] = row;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryAddAsync(string table, Row row, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Table(table).TryAdd((row.Pk, row.Rk), row));
        }
    }

    public Task DeleteAsync(string table, IReadOnlyCollection<Row> rows, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var row in rows)
            {
                Table(table).Remove((row.Pk, row.Rk));
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Row>> QueryAsync(string table, string pk, string? rkFrom, string? rkTo, int? take, CancellationToken ct)
    {
        lock (_gate)
        {
            var query = Table(table).Values.Where(r =>
                r.Pk == pk
                && (rkFrom is null || string.CompareOrdinal(r.Rk, rkFrom) >= 0)
                && (rkTo is null || string.CompareOrdinal(r.Rk, rkTo) < 0));
            return Task.FromResult<IReadOnlyList<Row>>([.. take is { } n ? query.Take(n) : query]);
        }
    }

    public Task<IReadOnlyList<Row>> QueryPartitionsAsync(string table, string pkFrom, string pkTo, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Row>>([.. Table(table).Values.Where(r =>
                string.CompareOrdinal(r.Pk, pkFrom) >= 0 && string.CompareOrdinal(r.Pk, pkTo) < 0)]);
        }
    }

    private sealed class KeyComparer : IComparer<(string Pk, string Rk)>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare((string Pk, string Rk) x, (string Pk, string Rk) y)
        {
            var pk = string.CompareOrdinal(x.Pk, y.Pk);
            return pk != 0 ? pk : string.CompareOrdinal(x.Rk, y.Rk);
        }
    }
}
