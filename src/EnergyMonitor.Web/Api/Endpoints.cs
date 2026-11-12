using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EnergyMonitor.Core.Abstractions;
using EnergyMonitor.Core.Alerts;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Core.Reports;
using EnergyMonitor.Core.Time;
using EnergyMonitor.Web.Auth;
using EnergyMonitor.Web.Eloverblik;
using EnergyMonitor.Web.Jobs;

namespace EnergyMonitor.Web.Api;

public sealed record DayValueDto(DateOnly Date, double? KWh, string Quality, int ExpectedIntervals, int ReceivedIntervals);

public sealed record HourValueDto(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double? KWh, string Quality);

public sealed record PropertyDto(
    string Id,
    string Name,
    bool MonitoringEnabled,
    bool HasMeteringPoint,
    string MeteringPoint,
    string Status,
    string StatusLabel,
    bool HasBaseline,
    DayValueDto? LatestComplete,
    double? Baseline,
    double? DeviationKWh,
    double? DeviationPercent,
    double? Sum7,
    double? Sum30,
    IReadOnlyList<DateOnly> PendingDays,
    bool IsStale,
    double? DataAgeHours,
    DateTimeOffset? LastSuccessfulSyncUtc,
    int OpenAlerts,
    IReadOnlyList<DayValueDto> Sparkline);

public sealed record AlertDto(
    string Id,
    string PropertyId,
    string PropertyName,
    string RuleType,
    string Severity,
    string State,
    string Label,
    DateOnly FirstDate,
    DateOnly LastDate,
    double? Observed,
    double? Baseline,
    double? Threshold,
    string Explanation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? AcknowledgedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record RuleDto(string Id, string Type, bool Enabled, RuleParams Params, DateTimeOffset UpdatedAtUtc);

public sealed record RuleUpdate(bool Enabled, RuleParams Params);

public sealed record PropertyUpdate(string Name, string? MeteringPointId, bool MonitoringEnabled);

public sealed record NotificationSettingsDto(bool DailyReportEnabled, IReadOnlyList<string> Recipients);

public static partial class Endpoints
{
    private const int MaxDayRange = 400;
    private const int MaxHourRange = 31;

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(AuthSetup.Viewer);
        var admin = api.MapGroup("/admin").RequireAuthorization(AuthSetup.Admin);

        api.MapGet("/me", (ClaimsPrincipal principal, Allowlist allowlist) =>
        {
            var user = allowlist.Resolve(principal)!;
            return Results.Ok(new { name = principal.Identity?.Name ?? user.Id, role = user.Role });
        });

        api.MapGet("/properties", async (IPropertyStore properties, SummaryService summaries, CancellationToken ct) =>
        {
            var lastSync = (await summaries.LastSuccessfulSyncAsync(ct))?.FinishedAtUtc;
            var list = new List<PropertyDto>();
            foreach (var property in await properties.ListAsync(ct))
            {
                var (summary, days) = await summaries.GetAsync(property, ct);
                list.Add(ToDto(summary, days, lastSync));
            }

            return Results.Ok(list);
        });

        api.MapGet("/properties/{id}/consumption", async (
            string id, string? from, string? to, string? resolution,
            IPropertyStore properties, IConsumptionStore consumption, CancellationToken ct) =>
        {
            if (await properties.GetAsync(id, ct) is not { } property)
            {
                return Results.NotFound();
            }

            if (!TryDate(from, out var fromDate) || !TryDate(to, out var toDate) || fromDate > toDate)
            {
                return Results.BadRequest(new { error = "invalid_range" });
            }

            var hourly = string.Equals(resolution, "hour", StringComparison.OrdinalIgnoreCase);
            if (toDate.DayNumber - fromDate.DayNumber >= (hourly ? MaxHourRange : MaxDayRange))
            {
                return Results.BadRequest(new { error = "range_too_long" });
            }

            var zone = LocalDay.Zone(property.TimeZone);
            if (hourly)
            {
                var intervals = await consumption.GetIntervalsAsync(
                    id, EloverblikMeasurementSource.Id, LocalDay.StartUtc(fromDate, zone), LocalDay.StartUtc(toDate.AddDays(1), zone), ct);
                return Results.Ok(new
                {
                    resolution = "hour",
                    items = intervals.OrderBy(i => i.StartUtc)
                        .Select(i => new HourValueDto(i.StartUtc, i.EndUtc, i.HasValue ? i.KWh : null, i.Quality.ToString())),
                });
            }

            var stored = (await consumption.GetDailyAsync(id, fromDate, toDate, ct)).ToDictionary(d => d.LocalDate);

            // A day without a stored row has not been fetched: it is reported as missing, never as zero.
            var items = LocalDay.Range(fromDate, toDate)
                .Select(d => stored.TryGetValue(d, out var day)
                    ? ToDto(day)
                    : new DayValueDto(d, null, nameof(DayQuality.Missing), 0, 0))
                .ToList();
            var complete = items.Where(i => i.Quality == nameof(DayQuality.Complete) && i.KWh is not null).Select(i => i.KWh!.Value).ToArray();
            return Results.Ok(new
            {
                resolution = "day",
                median = complete.Length > 0 ? Math.Round(Baseline.Median(complete), 3) : (double?)null,
                completeDays = complete.Length,
                items,
            });
        });

        api.MapGet("/properties/{id}/alerts", async (string id, IPropertyStore properties, IAlertStore alerts, CancellationToken ct) =>
            await properties.GetAsync(id, ct) is { } property
                ? Results.Ok((await alerts.GetEventsAsync(id, ct)).OrderByDescending(e => e.LastLocalDate).Select(e => ToDto(e, property.Name)))
                : Results.NotFound());

        api.MapGet("/alerts", async (string? state, IPropertyStore properties, IAlertStore alerts, CancellationToken ct) =>
        {
            var all = new List<AlertDto>();
            foreach (var property in await properties.ListAsync(ct))
            {
                all.AddRange((await alerts.GetEventsAsync(property.Id, ct)).Select(e => ToDto(e, property.Name)));
            }

            all.AddRange((await alerts.GetEventsAsync(AlertEvent.SystemPropertyId, ct)).Select(e => ToDto(e, "Datakilde")));
            var filtered = state?.ToLowerInvariant() switch
            {
                "open" => all.Where(a => a.State == nameof(AlertState.Open)),
                "closed" => all.Where(a => a.State == nameof(AlertState.Closed)),
                _ => all,
            };
            return Results.Ok(filtered.OrderByDescending(a => a.LastDate).ThenByDescending(a => a.CreatedAtUtc));
        });

        api.MapPost("/alerts/{id}/acknowledge", async (string id, IAlertStore alerts, TimeProvider time, CancellationToken ct) =>
        {
            if (await alerts.GetEventAsync(id, ct) is not { } e)
            {
                return Results.NotFound();
            }

            if (e.AcknowledgedAtUtc is null)
            {
                await alerts.UpdateEventAsync(e with { AcknowledgedAtUtc = time.GetUtcNow() }, ct);
            }

            return Results.NoContent();
        });

        api.MapGet("/properties/{id}/rules", async (string id, IPropertyStore properties, SummaryService summaries, CancellationToken ct) =>
            await properties.GetAsync(id, ct) is null
                ? Results.NotFound()
                : Results.Ok((await summaries.GetRulesAsync(id, ct)).Select(r => new RuleDto(r.Id, r.Type.ToString(), r.Enabled, r.Params, r.UpdatedAtUtc))));

        api.MapPut("/properties/{id}/rules/{ruleId}", async (
            string id, string ruleId, RuleUpdate update,
            IPropertyStore properties, SummaryService summaries, IAlertStore alerts, TimeProvider time, CancellationToken ct) =>
        {
            if (await properties.GetAsync(id, ct) is null
                || (await summaries.GetRulesAsync(id, ct)).FirstOrDefault(r => r.Id == ruleId) is not { } rule)
            {
                return Results.NotFound();
            }

            var p = update.Params;
            if (p is null || p.ThresholdPercent is < 0 or > 1000 || p.MinBaselineKWh is < 0 or > 1000
                || p.BaselineDays is < 1 or > 60 || p.MinBaselineDays < 1 || p.MinBaselineDays > p.BaselineDays
                || p.EscalateAfterDays is < 1 or > 30 || p.MissingHours is < 24 or > 720 || p.ConsecutiveFailures is < 1 or > 20)
            {
                return Results.BadRequest(new { error = "invalid_params" });
            }

            await alerts.UpsertRuleAsync(rule with { Enabled = update.Enabled, Params = p, UpdatedAtUtc = time.GetUtcNow() }, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.Admin);

        api.MapGet("/system/health", async (
            ISyncRunStore runs, IOutboxStore outbox, SyncService sync, EloverblikTokenStatus token,
            INotificationChannel channel, NotificationSettingsService settings, TimeProvider time, CancellationToken ct) =>
        {
            var recent = await runs.GetRecentAsync(EloverblikMeasurementSource.Id, 20, ct);
            var last = recent.FirstOrDefault();
            var pending = await outbox.ListPendingAsync(ct);
            var current = await settings.GetAsync(ct);

            // Deliberately coarse: status, counts and short error codes only. No tokens, ids or messages.
            return Results.Ok(new
            {
                serverTimeUtc = time.GetUtcNow(),
                syncRunning = sync.IsRunning,
                lastSync = last is null ? null : new { last.StartedAtUtc, last.FinishedAtUtc, status = last.Status.ToString(), last.IntervalCount, last.ErrorCode },
                lastSuccessfulSyncUtc = recent.FirstOrDefault(r => r.Status == SyncStatus.Succeeded)?.FinishedAtUtc,
                lastError = recent.FirstOrDefault(r => r.Status == SyncStatus.Failed) is { } failed ? new { failed.ErrorCode, at = failed.FinishedAtUtc } : null,
                token = token.Get(),
                notifications = new
                {
                    channel = channel.Name,
                    configured = channel.IsConfigured(current),
                    pending = pending.Count,
                    lastError = pending.LastOrDefault(m => m.LastError is not null)?.LastError,
                },
            });
        });

        admin.MapPost("/sync", (SyncService sync, MonitorPipeline pipeline, IHostApplicationLifetime lifetime) =>
        {
            if (sync.IsRunning)
            {
                return Results.Conflict(new { error = "sync_running" });
            }

            pipeline.StartInBackground(lifetime.ApplicationStopping);
            return Results.Accepted();
        });

        admin.MapPost("/notifications/test", async (INotificationChannel channel, NotificationSettingsService settings, TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), LocalDay.Zone(Property.DefaultTimeZone));
                await channel.SendAsync(
                    new NotificationMessage($"Energy Monitor · testbesked sendt {local:dd/MM} kl. {local:HH:mm}. Notifikationer virker."),
                    await settings.GetAsync(ct), ct);
                return Results.Ok(new { ok = true });
            }
            catch (NotificationException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        admin.MapGet("/settings/notifications", async (NotificationSettingsService settings, CancellationToken ct) =>
        {
            var s = await settings.GetAsync(ct);
            return Results.Ok(new NotificationSettingsDto(s.DailyReportEnabled, s.Recipients));
        });

        admin.MapPut("/settings/notifications", async (NotificationSettingsDto dto, NotificationSettingsService settings, CancellationToken ct) =>
        {
            var recipients = (dto.Recipients ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
            if (recipients.Count > 10 || recipients.Any(r => !ChatIdRegex().IsMatch(r)))
            {
                return Results.BadRequest(new { error = "invalid_recipients" });
            }

            await settings.SetAsync(new NotificationSettings(dto.DailyReportEnabled, recipients), ct);
            return Results.NoContent();
        });

        admin.MapGet("/report", async (ReportService reports, CancellationToken ct) =>
            Results.Ok(new { text = await reports.ComposeAsync(ct) }));

        admin.MapGet("/metering-points", async (EloverblikClient client, CancellationToken ct) =>
        {
            try
            {
                var points = await client.GetMeteringPointsAsync(ct);
                return Results.Ok(points.Select(p => new
                {
                    p.MeteringPointId,
                    masked = EloverblikClient.Mask(p.MeteringPointId),
                    address = string.Join(' ', new[] { p.StreetName, p.BuildingNumber, p.Postcode, p.CityName }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    type = p.TypeOfMP,
                    readingOccurrence = p.MeterReadingOccurrence,
                }));
            }
            catch (MeasurementSourceException ex)
            {
                return Results.Json(new { error = ex.ErrorCode }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        admin.MapPost("/properties", async (PropertyUpdate update, IPropertyStore properties, TimeProvider time, CancellationToken ct) =>
        {
            if (Validate(update) is { } error)
            {
                return error;
            }

            var id = Slug(update.Name);
            if (id.Length == 0 || id == AlertEvent.SystemPropertyId || await properties.GetAsync(id, ct) is not null)
            {
                return Results.Conflict(new { error = "name_taken" });
            }

            await properties.UpsertAsync(
                new Property(id, update.Name.Trim(), update.MeteringPointId?.Trim() ?? "", update.MonitoringEnabled, Property.DefaultTimeZone, time.GetUtcNow()), ct);
            return Results.Created($"/api/properties/{id}", new { id });
        });

        admin.MapPut("/properties/{id}", async (
            string id, PropertyUpdate update,
            IPropertyStore properties, IConsumptionStore consumption, IAlertStore alerts, IStateStore state, EnergyMonitor.Web.Pricing.PricingStore pricing, CancellationToken ct) =>
        {
            if (await properties.GetAsync(id, ct) is not { } property)
            {
                return Results.NotFound();
            }

            if (Validate(update) is { } error)
            {
                return error;
            }

            // An empty metering point in the request keeps the current one; the browser never needs the full id back.
            var meteringPoint = string.IsNullOrWhiteSpace(update.MeteringPointId) ? property.MeteringPointId : update.MeteringPointId.Trim();
            if (meteringPoint != property.MeteringPointId)
            {
                // Consumption of another meter must not be mixed into this property's history or baseline.
                await ClearDataAsync(id, consumption, alerts, state, ct);
                await pricing.DeleteAsync(id, ct);
            }

            await properties.UpsertAsync(
                property with { Name = update.Name.Trim(), MeteringPointId = meteringPoint, MonitoringEnabled = update.MonitoringEnabled }, ct);
            return Results.NoContent();
        });

        admin.MapDelete("/properties/{id}/data", async (
            string id, IPropertyStore properties, IConsumptionStore consumption, IAlertStore alerts, IStateStore state, EnergyMonitor.Web.Pricing.PricingStore pricing, CancellationToken ct) =>
        {
            if (await properties.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            await ClearDataAsync(id, consumption, alerts, state, ct);
            await pricing.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        admin.MapGet("/export", async (
            IPropertyStore properties, IConsumptionStore consumption, IAlertStore alerts, SummaryService summaries,
            NotificationSettingsService settings, TimeProvider time, EnergyMonitor.Web.Pricing.PricingStore pricing, CancellationToken ct) =>
        {
            var exported = new List<object>();
            foreach (var property in await properties.ListAsync(ct))
            {
                exported.Add(new
                {
                    property,
                    pricing = await pricing.GetConfigurationAsync(property.Id, ct),
                    priceSources = await pricing.GetSourcesAsync(property.Id, ct),
                    rules = await summaries.GetRulesAsync(property.Id, ct),
                    events = await alerts.GetEventsAsync(property.Id, ct),
                    days = await consumption.GetDailyAsync(property.Id, DateOnly.MinValue, DateOnly.MaxValue.AddDays(-1), ct),
                    intervals = await consumption.GetIntervalsAsync(
                        property.Id, EloverblikMeasurementSource.Id, time.GetUtcNow().AddYears(-6), time.GetUtcNow().AddDays(1), ct),
                });
            }

            return Results.Json(
                new
                {
                    exportedAtUtc = time.GetUtcNow(),
                    notifications = await settings.GetAsync(ct),
                    systemEvents = await alerts.GetEventsAsync(AlertEvent.SystemPropertyId, ct),
                    properties = exported,
                },
                Storage.StoreJson.Options);
        });
    }

    private static async Task ClearDataAsync(string id, IConsumptionStore consumption, IAlertStore alerts, IStateStore state, CancellationToken ct)
    {
        await consumption.DeleteAllAsync(id, ct);
        await alerts.DeleteAllAsync(id, ct);
        await state.DeleteAsync(SyncService.BackfillKey(id), ct);
    }

    private static IResult? Validate(PropertyUpdate update) =>
        string.IsNullOrWhiteSpace(update.Name) || update.Name.Length > 40 ? Results.BadRequest(new { error = "invalid_name" })
        : !string.IsNullOrWhiteSpace(update.MeteringPointId) && !MeteringPointRegex().IsMatch(update.MeteringPointId.Trim()) ? Results.BadRequest(new { error = "invalid_metering_point" })
        : null;

    private static string Slug(string name)
    {
        var lower = name.Trim().ToLowerInvariant().Replace("æ", "ae").Replace("ø", "oe").Replace("å", "aa");
        return SlugRegex().Replace(lower, "-").Trim('-');
    }

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static DayValueDto ToDto(DailyConsumption d) =>
        new(d.LocalDate, d.KWh, d.Quality.ToString(), d.ExpectedIntervals, d.ReceivedIntervals);

    private static PropertyDto ToDto(PropertySummary s, IReadOnlyList<DailyConsumption> days, DateTimeOffset? lastSync) => new(
        s.Property.Id,
        s.Property.Name,
        s.Property.MonitoringEnabled,
        !string.IsNullOrWhiteSpace(s.Property.MeteringPointId),
        EloverblikClient.Mask(s.Property.MeteringPointId),
        s.Status.ToString(),
        ReportComposer.StatusLabel(s.Status),
        s.HasBaseline,
        s.LatestComplete is null ? null : ToDto(s.LatestComplete),
        s.Baseline,
        s.DeviationKWh,
        s.DeviationPercent,
        s.Sum7,
        s.Sum30,
        s.PendingDays,
        s.IsStale,
        s.DataAgeHours,
        lastSync,
        s.OpenEvents.Count,
        [.. days.Where(d => d.IsComplete).OrderByDescending(d => d.LocalDate).Take(14).Reverse().Select(ToDto)]);

    private static AlertDto ToDto(AlertEvent e, string propertyName) => new(
        e.Id, e.PropertyId, propertyName, e.RuleType.ToString(), e.Severity.ToString(), e.State.ToString(),
        e.RuleType switch
        {
            RuleType.MissingData => "Data mangler",
            RuleType.SourceFailure or RuleType.TokenExpiry => "Teknisk",
            _ => e.Severity == AlertSeverity.Alarm ? "Alarm" : "Observer",
        },
        e.FirstLocalDate, e.LastLocalDate, e.Observed, e.Baseline, e.Threshold, e.Explanation,
        e.CreatedAtUtc, e.AcknowledgedAtUtc, e.ClosedAtUtc);

    [GeneratedRegex("^[0-9]{18}$")]
    private static partial Regex MeteringPointRegex();

    [GeneratedRegex("^(-?[0-9]{1,20}|@[A-Za-z0-9_]{3,64})$")]
    private static partial Regex ChatIdRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
