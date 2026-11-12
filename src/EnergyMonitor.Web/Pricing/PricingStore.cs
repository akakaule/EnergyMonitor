using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Storage;

namespace EnergyMonitor.Web.Pricing;

public sealed class PricingStore(ITableBackend tables)
{
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private sealed record ConfigurationManifest(int Chunks);
    public async Task<PricingConfiguration> GetConfigurationAsync(string propertyId, CancellationToken ct)
    {
        await _configurationGate.WaitAsync(ct);
        try
        {
            var marker = await tables.GetAsync("pricing", propertyId, "configuration", ct);
            if (marker is null) return PricingConfiguration.Empty;
            var manifest = StoreJson.Read<ConfigurationManifest>(marker);
            var chunks = await tables.QueryAsync("pricing", propertyId, "chunk|", "chunk|~", null, ct);
            if (chunks.Count != manifest.Chunks) return PricingConfiguration.Empty;
            return System.Text.Json.JsonSerializer.Deserialize<PricingConfiguration>(string.Concat(chunks.Select(StoreJson.Read<string>)), StoreJson.Options) ?? PricingConfiguration.Empty;
        }
        finally { _configurationGate.Release(); }
    }

    public async Task SaveConfigurationAsync(string propertyId, PricingConfiguration config, CancellationToken ct)
    {
        await _configurationGate.WaitAsync(ct);
        try
        {
            var chunks = StoreJson.Write(config).Chunk(4000).Select((chunk, index) =>
                new Row(propertyId, $"chunk|{index:D6}", StoreJson.Write(new string(chunk)))).ToArray();
            // Remove the completion marker first; an interrupted write fails closed as unconfigured.
            await tables.DeleteAsync("pricing", await tables.QueryAsync("pricing", propertyId, null, null, null, ct), ct);
            await tables.UpsertAsync("pricing", chunks, ct);
            await tables.UpsertAsync("pricing", [new(propertyId, "configuration", StoreJson.Write(new ConfigurationManifest(chunks.Length)))], ct);
        }
        finally { _configurationGate.Release(); }
    }

    public Task SavePricesAsync(IReadOnlyList<SpotPrice> prices, CancellationToken ct) =>
        tables.UpsertAsync("spotprices", prices.Select(p => new Row(p.Area + "|" + p.StartUtc.UtcDateTime.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture),
            StoreJson.Minute(p.StartUtc), StoreJson.Write(p))).ToArray(), ct);

    public async Task<IReadOnlyList<SpotPrice>> GetPricesAsync(string area, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = new List<SpotPrice>();
        var month = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (; month < to; month = month.AddMonths(1))
        {
            var rows = await tables.QueryAsync("spotprices", area + "|" + month.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture), StoreJson.Minute(from.AddHours(-1)), StoreJson.Minute(to), null, ct);
            result.AddRange(rows.Select(StoreJson.Read<SpotPrice>).Where(p => p.EndUtc > from));
        }
        return result;
    }

    public async Task SaveSourceAsync(string propertyId, PriceSourceStatus status, CancellationToken ct) =>
        await tables.UpsertAsync("pricesources", [new(propertyId, status.Source, StoreJson.Write(status))], ct);

    public async Task<IReadOnlyList<PriceSourceStatus>> GetSourcesAsync(string propertyId, CancellationToken ct) =>
        (await tables.QueryAsync("pricesources", propertyId, null, null, null, ct)).Select(StoreJson.Read<PriceSourceStatus>).ToArray();

    public async Task SaveImportedChargesAsync(string propertyId, NormalizedCharges charges, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        for (var month = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero); month < to; month = month.AddMonths(1))
        {
            var pk = propertyId + "|" + month.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
            var old = await ReadImportedAsync(pk, ct);
            var start = from > month ? from : month;
            var end = to < month.AddMonths(1) ? to : month.AddMonths(1);
            var rates = old.Rates.SelectMany(r => new[]
                {
                    r.FromUtc < start ? r with { ToUtc = r.ToUtc < start ? r.ToUtc : start } : null,
                    r.ToUtc > end ? r with { FromUtc = r.FromUtc > end ? r.FromUtc : end } : null,
                }.OfType<TariffRate>())
                .Concat(charges.Rates.Where(r => r.FromUtc < end && r.ToUtc > start).Select(r => r with
                { FromUtc = r.FromUtc < start ? start : r.FromUtc, ToUtc = r.ToUtc > end ? end : r.ToUtc })).ToArray();
            var links = old.Links.SelectMany(r => new[]
                {
                    r.FromUtc < start ? r with { ToUtc = r.ToUtc < start ? r.ToUtc : start } : null,
                    r.ToUtc > end ? r with { FromUtc = r.FromUtc > end ? r.FromUtc : end } : null,
                }.OfType<TariffLink>())
                .Concat(charges.Links.Where(r => r.FromUtc < end && r.ToUtc > start).Select(r => r with
                { FromUtc = r.FromUtc < start ? start : r.FromUtc, ToUtc = r.ToUtc > end ? end : r.ToUtc })).ToArray();
            var value = new NormalizedCharges(links, rates, charges.HasUnmappedCharges);
            await tables.DeleteAsync("importedcharges", await tables.QueryAsync("importedcharges", pk, null, null, null, ct), ct);
            await tables.UpsertAsync("importedcharges", StoreJson.Write(value).Chunk(4000).Select((chunk, n) => new Row(pk, $"{n:D6}", StoreJson.Write(new string(chunk)))).ToArray(), ct);
        }
    }

    private async Task<NormalizedCharges> ReadImportedAsync(string pk, CancellationToken ct)
    {
        var rows = await tables.QueryAsync("importedcharges", pk, null, null, null, ct);
        if (rows.Count == 0) return new([], [], true);
        try { return System.Text.Json.JsonSerializer.Deserialize<NormalizedCharges>(string.Concat(rows.Select(StoreJson.Read<string>)), StoreJson.Options) ?? new([], [], true); }
        catch (System.Text.Json.JsonException) { return new([], [], true); }
    }

    public async Task<NormalizedCharges> GetImportedChargesAsync(string propertyId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rates = new List<TariffRate>();
        var links = new List<TariffLink>();
        var unmapped = false;
        for (var month = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero); month < to; month = month.AddMonths(1))
        {
            var data = await ReadImportedAsync(propertyId + "|" + month.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture), ct);
            rates.AddRange(data.Rates.Where(r => r.FromUtc < to && r.ToUtc > from));
            links.AddRange(data.Links.Where(r => r.FromUtc < to && r.ToUtc > from));
            unmapped |= data.HasUnmappedCharges;
        }
        return new(links, rates, unmapped);
    }

    // Raw charge evidence is retained separately; only verified units/VAT become calculation rates.
    private readonly SemaphoreSlim _evidenceGate = new(1, 1);
    public async Task SaveChargeEvidenceAsync(string propertyId, string key, string json, CancellationToken ct)
    {
        await _evidenceGate.WaitAsync(ct);
        try
        {
            // 4k UTF-16 code units plus JSON escaping stay below Table's 64KiB string limit.
            // Latest response only; the normalized rate history is stored in pricing configuration.
            var chunks = json.Chunk(4000).Select((chunk, index) => new Row(propertyId,
                $"latest|{index:D6}", StoreJson.Write(new string(chunk)))).ToArray();
            await tables.DeleteAsync("chargeevidence", await tables.QueryAsync("chargeevidence", propertyId, null, null, null, ct), ct);
            await tables.UpsertAsync("chargeevidence", chunks, ct);
        }
        finally { _evidenceGate.Release(); }
    }

    public async Task<IReadOnlyList<System.Text.Json.JsonElement>> GetChargeEvidenceAsync(string propertyId, CancellationToken ct)
    {
        await _evidenceGate.WaitAsync(ct);
        try
        {
            var rows = await tables.QueryAsync("chargeevidence", propertyId, null, null, null, ct);
            if (rows.Count == 0) return [];
            var json = string.Concat(rows.OrderBy(r => r.Rk).Select(StoreJson.Read<string>));
            try { return [System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json)]; }
            catch (System.Text.Json.JsonException) { return []; } // interrupted evidence write: never used as prices
        }
        finally { _evidenceGate.Release(); }
    }

    public async Task DeleteAsync(string propertyId, CancellationToken ct)
    {
        await tables.DeleteAsync("importedcharges", await tables.QueryPartitionsAsync("importedcharges", propertyId + "|", propertyId + "|~", ct), ct);
        foreach (var table in new[] { "pricing", "pricesources", "chargeevidence" })
            await tables.DeleteAsync(table, await tables.QueryAsync(table, propertyId, null, null, null, ct), ct);
    }
}
