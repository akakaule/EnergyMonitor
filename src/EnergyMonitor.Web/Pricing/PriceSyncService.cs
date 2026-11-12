using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Eloverblik;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace EnergyMonitor.Web.Pricing;

public sealed class PricingOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.energidataservice.dk/";
    public int BackfillDays { get; set; } = 90;
}

public sealed class SpotPriceClient(IHttpClientFactory factory, IDelayer delayer)
{
    public async Task<IReadOnlyList<SpotPrice>> FetchAsync(string area, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from < new DateOnly(2025, 10, 1)) throw new MeasurementSourceException("spot_history_unsupported", "This importer supports quarter-hour DayAheadPrices since October 2025.");
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { PriceArea = new[] { area } }));
        var path = FormattableString.Invariant($"dataset/DayAheadPrices?start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}&filter={filter}&sort=TimeUTC%20ASC&limit=10000");
        for (var attempt = 0; ; attempt++)
        {
            using var response = await factory.CreateClient("spotprices").GetAsync(path, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 3)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60 * (1 << attempt));
                await delayer.DelayAsync(delay > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : delay, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new MeasurementSourceException("spot_http_" + (int)response.StatusCode, "Spot source failed.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var rows = json.RootElement.GetProperty("records");
            if (rows.GetArrayLength() >= 10000) throw new MeasurementSourceException("spot_truncated", "Spot source response exceeded limit.");
            return rows.EnumerateArray().Select(r =>
            {
                var start = DateTimeOffset.Parse(r.GetProperty("TimeUTC").GetString()! + "Z", System.Globalization.CultureInfo.InvariantCulture);
                // DayAheadPrices starts at the quarter-hour market transition on 1 October 2025.
                return new SpotPrice(r.GetProperty("PriceArea").GetString()!, start, start.AddMinutes(15), r.GetProperty("DayAheadPriceDKK").GetDecimal() / 1000m);
            }).ToArray();
        }
    }
}

public sealed class PriceSyncService(IOptions<PricingOptions> options, IPropertyStore properties,
    PricingStore store, SpotPriceClient spot, EloverblikClient eloverblik, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task RunAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || !await _gate.WaitAsync(0, ct)) return;
        try
        {
            var today = LocalDay.Of(time.GetUtcNow(), LocalDay.Zone(Property.DefaultTimeZone));
            // Bounded 90-day replay is idempotent and revisits source corrections and earlier gaps.
            var from = today.AddDays(-Math.Clamp(options.Value.BackfillDays, 1, 90));
            foreach (var property in await properties.ListAsync(ct))
            {
                var config = await store.GetConfigurationAsync(property.Id, ct);
                foreach (var area in config.Agreements.Select(a => a.Area).Distinct())
                    await FetchAsync(property.Id, "Energinet." + area, async () =>
                    {
                        var prices = await spot.FetchAsync(area, from, today.AddDays(2), ct);
                        await store.SavePricesAsync(prices, ct);
                        var ordered = prices.OrderBy(p => p.StartUtc).ToArray();
                        var contiguous = ordered.Length > 0 && !ordered.Zip(ordered.Skip(1)).Any(p => p.First.EndUtc != p.Second.StartUtc);
                        return (ordered.FirstOrDefault()?.StartUtc, ordered.LastOrDefault()?.EndUtc, contiguous ? null : "spot_incomplete");
                    }, ct);
                if (!string.IsNullOrWhiteSpace(property.MeteringPointId))
                    await FetchAsync(property.Id, "Eloverblik.charges", async () =>
                    {
                        var evidence = await eloverblik.GetChargeEvidenceAsync(property.MeteringPointId, from, today, ct);
                        await store.SaveChargeEvidenceAsync(property.Id, from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), evidence.Json, ct);
                        if (!evidence.Historical) return ((DateTimeOffset?)null, (DateTimeOffset?)null, "current_charges_only");
                        var start = LocalDay.StartUtc(from, LocalDay.Zone(Property.DefaultTimeZone));
                        var end = LocalDay.StartUtc(today, LocalDay.Zone(Property.DefaultTimeZone));
                        var charges = ChargeNormalizer.Normalize(evidence.Json, config.TariffMappings, start, end);
                        await store.SaveImportedChargesAsync(property.Id, charges, start, end, ct);
                        return ((DateTimeOffset?)null, (DateTimeOffset?)null, charges.HasUnmappedCharges ? "charge_units_unverified" : (string?)null);
                    }, ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task FetchAsync(string id, string source, Func<Task<(DateTimeOffset? From, DateTimeOffset? To, string? Error)>> fetch, CancellationToken ct)
    {
        var previous = (await store.GetSourcesAsync(id, ct)).FirstOrDefault(s => s.Source == source);
        try
        {
            var result = await fetch();
            await store.SaveSourceAsync(id, new(source, time.GetUtcNow(), result.From, result.To, result.Error), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No upstream exception message, token, or meter identifier is persisted or returned.
            await store.SaveSourceAsync(id, new(source, previous?.LastSuccessUtc, previous?.FromUtc, previous?.ToUtc,
                ex is MeasurementSourceException e ? e.ErrorCode : "price_source_failed"), ct);
        }
    }
}
