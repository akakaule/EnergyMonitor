using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Auth;

namespace EnergyMonitor.Web.Pricing;

public static class PricingEndpoints
{
    public static void MapPricing(this WebApplication app)
    {
        app.MapGet("/api/features", () => new { pricing = app.Configuration.GetValue<bool>("Pricing:Enabled"), mcp = app.Configuration.GetValue<bool>("Mcp:Enabled") }).RequireAuthorization(AuthSetup.Viewer);
        if (!app.Configuration.GetValue<bool>("Pricing:Enabled")) return;
        var api = app.MapGroup("/api/energy").RequireAuthorization(AuthSetup.Viewer);
        api.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_range" }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        api.MapGet("/overview", (string from, string to, EnergyQueryService q, CancellationToken ct) => q.OverviewAsync(from, to, ct));
        api.MapGet("/{id}/costs", (string id, string from, string to, EnergyQueryService q, CancellationToken ct) => q.CostsAsync(id, from, to, ct));
        api.MapGet("/{id}/consumption", (string id, string from, string to, string resolution, EnergyQueryService q, CancellationToken ct) => q.ConsumptionAsync(id, from, to, resolution, ct));
        api.MapGet("/{id}/breakdown", (string id, string from, string to, EnergyQueryService q, CancellationToken ct) => q.BreakdownAsync(id, from, to, ct));
        api.MapGet("/{id}/status", (string id, string from, string to, EnergyQueryService q, CancellationToken ct) => q.StatusAsync(id, from, to, ct));
        api.MapGet("/{id}/compare", (string id, string from, string to, string previousFrom, string previousTo, EnergyQueryService q, CancellationToken ct) => q.CompareAsync(id, from, to, previousFrom, previousTo, ct));
        api.MapGet("/{id}/configuration", async (string id, EnergyQueryService q, PricingStore store, CancellationToken ct) =>
        {
            await q.PropertyAsync(id, ct);
            return await store.GetConfigurationAsync(id, ct);
        });
        api.MapGet("/{id}/charge-evidence", async (string id, EnergyQueryService q, PricingStore store, CancellationToken ct) =>
        {
            await q.PropertyAsync(id, ct);
            return await store.GetChargeEvidenceAsync(id, ct);
        }).RequireAuthorization(AuthSetup.Admin);
        api.MapPut("/{id}/configuration", async (string id, PricingConfiguration config, EnergyQueryService q, PricingStore store, CancellationToken ct) =>
        {
            await q.PropertyAsync(id, ct);
            if (!EnergyQueryService.ValidConfiguration(config)) return Results.BadRequest(new { error = "invalid_pricing" });
            await store.SaveConfigurationAsync(id, config, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.Admin);
    }
}
