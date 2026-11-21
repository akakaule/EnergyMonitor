using System.ComponentModel;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;

namespace EnergyMonitor.Web.Pricing;

public static class EnergyMcpSetup
{
    public static void AddEnergyMcp(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Mcp:Enabled")) return;
        if (AuthSetup.IsNone(builder.Configuration))
        {
            // Without sign-in the app is private by network, and so is /mcp: no OAuth to configure.
            AddTools(builder);
            return;
        }

        var authority = builder.Configuration["Mcp:Authority"];
        var audience = builder.Configuration["Mcp:Audience"];
        var resource = builder.Configuration["Mcp:Resource"];
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != "https"
            || string.IsNullOrWhiteSpace(audience) || !Uri.TryCreate(resource, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidOperationException("Mcp:Authority, Audience and HTTPS Resource must be configured when MCP is enabled.");
        builder.Services.AddAuthentication().AddJwtBearer("McpBearer", o =>
        {
            o.Authority = authority;
            o.Audience = audience;
            o.MapInboundClaims = false;
            o.RequireHttpsMetadata = true;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30), ValidAudience = audience,
            };
            o.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{url.GetLeftPart(UriPartial.Authority)}/.well-known/oauth-protected-resource/mcp\"";
                    return Task.CompletedTask;
                },
            };
        });
        var scope = builder.Configuration["Mcp:ReadScope"] ?? "energy.read";
        builder.Services.AddAuthorizationBuilder().AddPolicy("mcp-read", p => p
            .AddAuthenticationSchemes("McpBearer").RequireAuthenticatedUser()
            .AddRequirements(new RoleRequirement(false))
            .RequireAssertion(c => c.User.FindAll("scope").Concat(c.User.FindAll("scp"))
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(scope)));
        AddTools(builder);
    }

    private static void AddTools(WebApplicationBuilder builder)
    {
        var mcp = builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<EnergyTools>();
        if (HeatPump.HeatPumpEndpoints.IsEnabled(builder.Configuration))
        {
            mcp.WithTools<HeatPump.HeatPumpTools>();
        }
    }

    public static void MapEnergyMcp(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Mcp:Enabled"))
        {
            app.Map("/mcp", () => Results.NotFound()).AllowAnonymous();
            return;
        }

        if (AuthSetup.IsNone(app.Configuration))
        {
            app.Map("/.well-known/oauth-protected-resource/mcp", () => Results.NotFound()).AllowAnonymous();
            app.MapMcp("/mcp").RequireAuthorization(AuthSetup.Viewer);
            return;
        }
        app.MapGet("/.well-known/oauth-protected-resource/mcp", () => new
        {
            resource = app.Configuration["Mcp:Resource"],
            authorization_servers = new[] { app.Configuration["Mcp:Authority"] },
            scopes_supported = new[] { app.Configuration["Mcp:ReadScope"] ?? "energy.read" },
            bearer_methods_supported = new[] { "header" },
        }).AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization("mcp-read");
    }
}

// Tools with an untyped result return their JSON as text and declare no output schema: the SDK would
// otherwise announce {"$defs":{"output":true}}, which Claude Code rejects for the whole tool list.
[McpServerToolType]
public sealed class EnergyTools(EnergyQueryService queries)
{
    [McpServerTool(Name = "get_energy_overview", ReadOnly = true), Description("Homes, consumption and costs. Dates YYYY-MM-DD inclusive, maximum 90 days.")]
    public Task<object> Overview(string from, string to, CancellationToken ct) => Safe(() => queries.OverviewAsync(from, to, ct));

    [McpServerTool(Name = "get_consumption", ReadOnly = true), Description("Consumption with data quality. Resolution day or interval. Maximum 90 inclusive Danish days.")]
    public Task<object> Consumption(string propertyId, string from, string to, string resolution, CancellationToken ct) => Safe(() => queries.ConsumptionAsync(propertyId, from, to, resolution, ct));

    [McpServerTool(Name = "get_costs", ReadOnly = true, UseStructuredContent = true), Description("DKK cost components, known subtotal, coverage and estimates. Total is null if incomplete. Maximum 90 inclusive Danish days.")]
    public Task<CostResult> Costs(string propertyId, string from, string to, CancellationToken ct) => Safe(() => queries.CostsAsync(propertyId, from, to, ct));

    [McpServerTool(Name = "get_price_estimate", ReadOnly = true, UseStructuredContent = true), Description("Approximate DKK price incl. VAT (\"ca. pris\"), always with a total: hourly consumption x spot price, the supplier's registered markup and subscription, and the net tariffs, Energinet tariffs, electricity tax and subscriptions Eloverblik reports today, applied to the whole period. Components are excl. VAT plus a Vat component. 'assumptions' lists what is assumed (current_tariffs, supplier_markup_missing, supplier_subscription_missing, charges_missing). Maximum 90 inclusive Danish days.")]
    public Task<ApproximateEstimate> Estimate(string propertyId, string from, string to, CancellationToken ct) => Safe(() => queries.EstimateAsync(propertyId, from, to, ct));

    [McpServerTool(Name = "get_spot_prices", ReadOnly = true, UseStructuredContent = true), Description("Electricity price per kWh hour by hour, also for today and tomorrow (tomorrow is published around 13:00 Danish time). Per hour: the day-ahead spot price and an approximate total in DKK incl. VAT (spot or fixed price, the supplier's registered markup, and the tariffs and taxes Eloverblik reports today), with the parts excl. VAT. Per day: status (Complete, Partial, Pending), average, min, max, the cheapest and the most expensive period of 'windowHours' consecutive hours (for today: among the hours not yet over) and the number of hours with a negative spot price. 'assumptions' lists what is assumed (current_tariffs, charges_missing, supplier_markup_missing); no_agreement means the property has no registered agreement and no prices. Dates YYYY-MM-DD inclusive, maximum 7 days, at most two days ahead.")]
    public Task<PriceView> SpotPrices(string propertyId, string from, string to, CancellationToken ct) => Safe(() => queries.PricesAsync(propertyId, from, to, ct));

    [McpServerTool(Name = "compare_periods", ReadOnly = true), Description("Compare consumption and costs with independent coverage. Each period at most 90 inclusive Danish days.")]
    public Task<object> Compare(string propertyId, string from, string to, string previousFrom, string previousTo, CancellationToken ct) => Safe(() => queries.CompareAsync(propertyId, from, to, previousFrom, previousTo, ct));

    [McpServerTool(Name = "get_price_breakdown", ReadOnly = true), Description("Registered agreement history, tariff rates and source status. Maximum 90 inclusive Danish days.")]
    public Task<object> Breakdown(string propertyId, string from, string to, CancellationToken ct) => Safe(() => queries.BreakdownAsync(propertyId, from, to, ct));

    [McpServerTool(Name = "get_data_status", ReadOnly = true), Description("Missing data, coverage and last successful price sync. Maximum 90 inclusive Danish days.")]
    public Task<object> Status(string propertyId, string from, string to, CancellationToken ct) => Safe(() => queries.StatusAsync(propertyId, from, to, ct));

    private static async Task<T> Safe<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ArgumentException) { throw new ModelContextProtocol.McpException("invalid_range_or_resolution"); }
        catch (KeyNotFoundException) { throw new ModelContextProtocol.McpException("property_not_found"); }
    }
}
