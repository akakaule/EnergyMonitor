using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Time;

namespace EnergyMonitor.Web.Eloverblik;

public sealed class EloverblikMeasurementSource(EloverblikClient client, Microsoft.Extensions.Options.IOptionsMonitor<EloverblikOptions>? options = null) : IMeasurementSource
{
    public const string Id = "eloverblik";

    public string SourceId => Id;

    public async Task<IReadOnlyList<Measurement>> FetchAsync(Property property, DateOnly fromLocal, DateOnly toLocal, CancellationToken ct)
    {
        var zone = LocalDay.Zone(property.TimeZone);
        IReadOnlyList<TimeSeriesPoint> points;
        try
        {
            points = await client.GetTimeSeriesAsync(property.MeteringPointId, fromLocal, toLocal, options?.CurrentValue.PreferredAggregation == "Quarter" ? "Quarter" : "Hour", zone, ct);
        }
        catch (MeasurementSourceException ex) when (ex.ErrorCode == $"eloverblik_{EloverblikClient.AggregationNotSupported}")
        {
            // Not every metering point is read hourly. Daily values are stored as daily values;
            // hours are never made up from them.
            if (options?.CurrentValue.PreferredAggregation == "Quarter")
            {
                try { points = await client.GetTimeSeriesAsync(property.MeteringPointId, fromLocal, toLocal, "Hour", zone, ct); }
                catch (MeasurementSourceException hourly) when (hourly.ErrorCode == $"eloverblik_{EloverblikClient.AggregationNotSupported}")
                { points = await client.GetTimeSeriesAsync(property.MeteringPointId, fromLocal, toLocal, "Day", zone, ct); }
            }
            else points = await client.GetTimeSeriesAsync(property.MeteringPointId, fromLocal, toLocal, "Day", zone, ct);
        }

        return [.. points.Select(p =>
        {
            var quality = MapQuality(p.QualityCode, p.Quantity);
            return new Measurement(
                Id, property.Id, Measurement.EnergyConsumption,
                quality is IntervalQuality.Missing ? null : p.Quantity,
                "kWh", p.StartUtc, quality, p.EndUtc, p.Resolution);
        })];
    }

    /// <summary>
    /// Eloverblik quality codes: A01 adjusted, A02 not available, A03 estimated, A04 measured,
    /// A05 incomplete. Anything unknown, or a point without a quantity, is treated as missing.
    /// </summary>
    public static IntervalQuality MapQuality(string code, double? quantity) => (code, quantity) switch
    {
        (_, null) => IntervalQuality.Missing,
        ("A04", _) => IntervalQuality.Measured,
        ("A03", _) => IntervalQuality.Estimated,
        ("A01", _) => IntervalQuality.Adjusted,
        ("A05", _) => IntervalQuality.Incomplete,
        _ => IntervalQuality.Missing,
    };
}

/// <summary>What is known about the personal refresh token, without exposing it.</summary>
public sealed record TokenStatus(bool Configured, DateTimeOffset? ExpiresAtUtc, int? DaysLeft, string State);

public sealed class EloverblikTokenStatus(Microsoft.Extensions.Options.IOptionsMonitor<EloverblikOptions> options, TimeProvider time)
{
    public DateTimeOffset? ExpiresAt => JwtExpiry.TryRead(options.CurrentValue.RefreshToken);

    public TokenStatus Get()
    {
        var token = options.CurrentValue.RefreshToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TokenStatus(false, null, null, "missing");
        }

        if (JwtExpiry.TryRead(token) is not { } expires)
        {
            // Expiry is not readable: renewal has to be tracked manually (see README).
            return new TokenStatus(true, null, null, "unknown");
        }

        var days = (int)Math.Floor((expires - time.GetUtcNow()).TotalDays);
        var state = days < 0 ? "expired" : days <= 30 ? "expiring" : "ok";
        return new TokenStatus(true, expires, days, state);
    }
}
