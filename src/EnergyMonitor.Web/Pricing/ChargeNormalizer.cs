using System.Text.Json;
using EnergyMonitor.Core.Pricing;

namespace EnergyMonitor.Web.Pricing;

public sealed record NormalizedCharges(IReadOnlyList<TariffLink> Links, IReadOnlyList<TariffRate> Rates, bool HasUnmappedCharges);

public static class ChargeNormalizer
{
    public static NormalizedCharges Normalize(string json, IReadOnlyList<TariffMapping> mappings,
        DateTimeOffset from, DateTimeOffset to)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("result");
        var links = new List<TariffLink>();
        var rates = new List<TariffRate>();
        var unmapped = false;
        foreach (var result in root.GetProperty("results").EnumerateArray())
        foreach (var link in Array(result, "chargeLinks"))
        {
            var id = link.GetProperty("chargeIdentifier");
            var code = id.GetProperty("code").GetString()!;
            var owner = id.GetProperty("owner").GetString()!;
            var type = id.GetProperty("type").GetString()!;
            var matches = mappings.Where(m => m.Code == code && m.Owner == owner && m.Type == type).ToArray();
            if (matches.Length == 0) unmapped = true;
            foreach (var period in Array(link, "chargeLinkPeriods"))
            {
                var start = Max(period.GetProperty("from").GetDateTimeOffset(), from);
                var end = Min(End(period, to), to);
                if (end <= start) continue;
                var factor = period.GetProperty("factor").GetInt32();
                links.Add(new(code, owner, type, start, end, factor));
                foreach (var information in Array(root, "chargeInformations"))
                {
                    var infoId = information.GetProperty("chargeIdentifier");
                    if (infoId.GetProperty("code").GetString() != code || infoId.GetProperty("owner").GetString() != owner
                        || infoId.GetProperty("type").GetString() != type) continue;
                    foreach (var point in Array(information, "chargeSeriesPoints"))
                    foreach (var mapping in matches)
                    {
                        var rateStart = Max(Max(start, point.GetProperty("from").GetDateTimeOffset()), mapping.FromUtc);
                        var rateEnd = Min(Min(end, End(point, end)), mapping.ToUtc);
                        if (rateEnd <= rateStart) continue;
                        rates.Add(new(code, owner, mapping.Component, rateStart, rateEnd,
                            point.GetProperty("price").GetDecimal() * factor, mapping.Unit, mapping.Vat, "Eloverblik.historical"));
                    }
                }
            }
        }
        return new(links.Distinct().ToArray(), rates.Distinct().ToArray(), unmapped);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static DateTimeOffset End(JsonElement element, DateTimeOffset fallback) =>
        element.TryGetProperty("to", out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : fallback;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
