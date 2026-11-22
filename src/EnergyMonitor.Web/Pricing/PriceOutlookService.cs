using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Core.Reports;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Jobs;
using EnergyMonitor.Web.Storage;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Web.Pricing;

/// <summary>
/// The hourly prices as Telegram text: a line per price group in the daily report, and one message about
/// tomorrow on the days whose prices stand out.
/// </summary>
public sealed class PriceOutlookService(
    IPropertyStore properties,
    EnergyQueryService queries,
    IOutboxStore outbox,
    NotificationSettingsService settings,
    IOptions<PricingOptions> options,
    TimeProvider time)
{
    /// <summary>The comparison with the latest days needs at least this many of them to have complete prices.</summary>
    public const int MinRecentDays = 3;
    private const int RecentDays = 7;

    /// <summary>
    /// The day's prices for the properties that have an agreement. Properties with the same terms and
    /// tariffs pay the same price and share a group.
    /// </summary>
    public async Task<IReadOnlyList<(PriceGroup Group, Property First)>> GroupsAsync(DateOnly day, CancellationToken ct)
    {
        var groups = new List<(string Key, List<string> Names, PriceGroup Group, Property First)>();
        foreach (var property in await properties.ListAsync(ct))
        {
            var (inputs, days) = await queries.DayPricesAsync(property, day, day, ct);
            if (inputs.Terms is null || days is not [var prices])
            {
                continue;
            }

            var key = property.TimeZone + StoreJson.Write(new { inputs.Terms, inputs.Rates });
            if (groups.FirstOrDefault(g => g.Key == key) is { Names: { } names })
            {
                names.Add(property.Name);
                continue;
            }

            names = [property.Name];
            groups.Add((key, names, new(names, inputs.Terms.Area, prices, LocalDay.Zone(property.TimeZone)), property));
        }

        return [.. groups.Select(g => (g.Group, g.First))];
    }

    /// <summary>The report's lines about today. Nothing while pricing is off or today's prices are incomplete.</summary>
    public async Task<IReadOnlyList<string>> TodayLinesAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return [];
        }

        var today = LocalDay.Of(time.GetUtcNow(), LocalDay.Zone(Property.DefaultTimeZone));
        return [.. (await GroupsAsync(today, ct)).Select(g => PriceText.TodayLine(g.Group)).OfType<string>()];
    }

    /// <summary>
    /// Queues the message about tomorrow when every group's prices are complete and at least one stands out.
    /// The message id is the date, so a day gets at most one message however often prices are fetched.
    /// </summary>
    public async Task<bool> EnqueueTomorrowAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || !(await settings.GetAsync(ct)).TomorrowPricesEnabled)
        {
            return false;
        }

        var today = LocalDay.Of(time.GetUtcNow(), LocalDay.Zone(Property.DefaultTimeZone));
        var tomorrow = today.AddDays(1);
        var groups = await GroupsAsync(tomorrow, ct);
        if (groups.Count == 0 || groups.Any(g => g.Group.Day.Status != DayPrices.Complete))
        {
            return false;
        }

        var lines = new List<(PriceGroup Group, IReadOnlyList<string> Reasons)>();
        foreach (var (group, first) in groups)
        {
            var (_, recent) = await queries.DayPricesAsync(first, today.AddDays(1 - RecentDays), today, ct);
            var averages = recent.Select(d => d.Average).OfType<decimal>().ToArray();
            lines.Add((group, PriceText.Remarkable(group, averages.Length >= MinRecentDays ? averages.Average() : null,
                options.Value.NotifyDeviationPercent, options.Value.NotifyHighPrice)));
        }

        return lines.Any(l => l.Reasons.Count > 0)
            && await outbox.TryEnqueueAsync(new OutboxMessage($"prices|{StoreJson.Day(tomorrow)}", PriceText.TomorrowMessage(tomorrow, lines), time.GetUtcNow()), ct);
    }
}
