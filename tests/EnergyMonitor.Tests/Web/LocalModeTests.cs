using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Web.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnergyMonitor.Tests.Web;

/// <summary>
/// The app as it runs on the private Linux machine: reachable only through Tailscale, so there is no sign-in
/// and every request acts as the household's administrator.
/// </summary>
public class LocalModeTests
{
    [Fact]
    public async Task EveryRequest_IsTheAdministrator_WithoutSigningIn()
    {
        using var app = new AppFactory(local: true);
        using var client = app.ClientFor(null);

        var me = await client.GetJsonAsync("/api/me");
        Assert.Equal(UserRoles.Admin, me.GetProperty("role").GetString());

        var settings = await client.GetAsync("/api/admin/settings/notifications");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    [Fact]
    public async Task StateChangingCalls_StillNeedTheRequestHeader()
    {
        using var app = new AppFactory(local: true);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/admin/sync", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_IsOpen_WithoutOAuthSettings()
    {
        using var app = new AppFactory(pricing: true, mcp: true, local: true);
        await app.SeedPropertiesAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var tools = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
        Assert.Contains("get_costs", await tools.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode);
    }

    [Fact]
    public async Task Mcp_ToolList_HasOnlyObjectOutputSchemas_SoClientsAcceptIt()
    {
        using var app = new AppFactory(pricing: true, mcp: true, local: true);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var text = await response.Content.ReadAsStringAsync();
        if (text.StartsWith("event:", StringComparison.Ordinal))
            text = text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var json = System.Text.Json.JsonDocument.Parse(text);
        var tools = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();

        Assert.Equal(7, tools.Length);
        // Claude Code rejects the whole list if any outputSchema is, or contains, a boolean schema such as
        // {"$defs":{"output":true}} (what an untyped result produces). Such tools must not declare one.
        static bool HasBooleanSchema(System.Text.Json.JsonElement e) => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => true,
            System.Text.Json.JsonValueKind.Object => e.EnumerateObject().Any(p => p.Name is not ("default" or "const" or "enum" or "examples") && HasBooleanSchema(p.Value)),
            System.Text.Json.JsonValueKind.Array => e.EnumerateArray().Any(HasBooleanSchema),
            _ => false,
        };
        Assert.All(tools, t =>
        {
            if (!t.TryGetProperty("outputSchema", out var schema)) return;
            Assert.Equal(System.Text.Json.JsonValueKind.Object, schema.ValueKind);
            Assert.False(HasBooleanSchema(schema), $"{t.GetProperty("name").GetString()} has a boolean subschema: {schema.GetRawText()}");
        });
    }

    [Fact]
    public async Task Mcp_GivesTheSameApproximatePrice_AsTheApp()
    {
        using var app = new AppFactory(pricing: true, mcp: true, local: true);
        await app.SeedPropertiesAsync();
        await app.SyncAsync();
        var store = app.Get<EnergyMonitor.Web.Pricing.PricingStore>();
        var start = DateTimeOffset.Parse("2026-10-31T23:00:00Z");
        await store.SavePricesAsync([.. Enumerable.Range(0, 24).Select(h => new EnergyMonitor.Core.Pricing.SpotPrice("DK1", start.AddHours(h), start.AddHours(h + 1), 1m))], default);
        await store.SaveChargeEvidenceAsync("hjem", "2026-11-01", ApproximatePricingTests.CurrentCharges, default);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var call = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "get_price_estimate", arguments = new { propertyId = "hjem", from = "2026-11-01", to = "2026-11-01" } } });
        var text = await call.Content.ReadAsStringAsync();
        if (text.StartsWith("event:", StringComparison.Ordinal))
            text = text.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var json = System.Text.Json.JsonDocument.Parse(text);
        var structured = json.RootElement.GetProperty("result").GetProperty("structuredContent");

        var expected = await app.Get<EnergyMonitor.Web.Pricing.EnergyQueryService>().EstimateAsync("hjem", "2026-11-01", "2026-11-01", default);
        Assert.True(expected.Total > 0);
        Assert.Equal(expected.Total, structured.GetProperty("total").GetDecimal());
        Assert.Contains("current_tariffs", structured.GetProperty("assumptions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void DevelopmentSignIn_IsStillRefused_OutsideDevelopment()
    {
        using var app = new AppFactory().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("only allowed in the Development environment", error.ToString());
    }
}
