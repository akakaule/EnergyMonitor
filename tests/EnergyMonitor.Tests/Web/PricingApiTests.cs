using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using EnergyMonitor.Core.Pricing;
using EnergyMonitor.Web.Pricing;
using EnergyMonitor.Web.Storage;

namespace EnergyMonitor.Tests.Web;

public sealed class PricingApiTests
{
    private static PricingConfiguration Configuration => new(
        [new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, "DK1", "Fixed", 1m, 0m, 2m, 60, 0.25m)],
        [], [], true);

    [Fact]
    public async Task Costs_RequiresLogin_ConfigRequiresAdmin_AndRangeIsBounded()
    {
        using var app = new AppFactory(pricing: true);
        await app.SeedPropertiesAsync();
        using var anonymous = app.ClientFor(null);
        using var viewer = app.ClientFor(AppFactory.Viewer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/energy/hjem/costs?from=2026-11-01&to=2026-11-02")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/energy/hjem/configuration", Configuration)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/energy/hjem/costs?from=2026-01-01&to=2026-11-02")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/energy/absent/costs?from=2026-11-01&to=2026-11-02")).StatusCode);
    }

    [Fact]
    public async Task SavedTerms_CorrectedConsumption_AndMeterChange_AreReflectedImmediately()
    {
        using var app = new AppFactory(pricing: true);
        await app.SeedPropertiesAsync();
        await app.SyncAsync();
        using var client = app.ClientFor(AppFactory.Admin);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/energy/hjem/configuration", Configuration)).StatusCode);
        var result = await client.GetFromJsonAsync<CostResult>("/api/energy/hjem/costs?from=2026-11-01&to=2026-11-01", StoreJson.Options);
        Assert.NotNull(result!.Total);
        Assert.Equal((result.KnownKWh + 2) * 1.25m, result.Total.Value, 8);
        await client.PutAsJsonAsync("/api/admin/properties/hjem", new { name = "Hjem", meteringPointId = "571313100000000999", monitoringEnabled = true });
        Assert.Empty((await app.Get<PricingStore>().GetConfigurationAsync("hjem", default)).Agreements);
    }

    [Fact]
    public async Task OverlappingTermsAndDuplicateTariffs_AreRejected()
    {
        using var app = new AppFactory(pricing: true);
        await app.SeedPropertiesAsync();
        using var client = app.ClientFor(AppFactory.Admin);
        var config = Configuration with { Agreements = [Configuration.Agreements[0], Configuration.Agreements[0]] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/energy/hjem/configuration", config)).StatusCode);
    }

    [Fact]
    public async Task Mcp_UsesOnlyBearer_ValidatesClaims_AndMatchesApi()
    {
        using var app = new AppFactory(pricing: true, mcp: true);
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("test-key-at-least-32-characters-long-123456"));
        using var configured = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>("McpBearer", o =>
        {
            o.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = "https://issuer.test" };
            o.TokenValidationParameters.IssuerSigningKey = key;
            o.TokenValidationParameters.ValidIssuer = "https://issuer.test";
        })));
        await app.SeedPropertiesAsync();
        await app.Get<PricingStore>().SaveConfigurationAsync("hjem", Configuration, default);
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", AppFactory.Admin);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var noBearer = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);
        Assert.Contains("resource_metadata", noBearer.Headers.WwwAuthenticate.ToString());
        var metadata = await client.GetStringAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Contains("https://issuer.test", metadata);
        string Token(string subject = AppFactory.Admin, string scope = "energy.read", string audience = "energy", bool expired = false) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = "https://issuer.test", Audience = audience,
                Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("scope", scope)]),
                IssuedAt = DateTime.UtcNow.AddHours(-2), NotBefore = DateTime.UtcNow.AddHours(-2),
                Expires = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
        foreach (var (token, status) in new[] { (Token(audience: "other"), HttpStatusCode.Unauthorized),
            (Token(expired: true), HttpStatusCode.Unauthorized), (Token(scope: "write"), HttpStatusCode.Forbidden),
            (Token(subject: AppFactory.Stranger), HttpStatusCode.Forbidden) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(status, (await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var tools = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        tools.EnsureSuccessStatusCode();
        var list = await tools.Content.ReadAsStringAsync();
        Assert.Contains("get_costs", list);
        Assert.DoesNotContain("save", list);
        var call = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "get_costs", arguments = new { propertyId = "hjem", from = "2026-11-01", to = "2026-11-01" } } });
        call.EnsureSuccessStatusCode();
        var responseText = await call.Content.ReadAsStringAsync();
        if (responseText.StartsWith("event:", StringComparison.Ordinal))
            responseText = responseText.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var json = JsonDocument.Parse(responseText);
        var structured = json.RootElement.GetProperty("result").GetProperty("structuredContent");
        var api = await app.Get<EnergyQueryService>().CostsAsync("hjem", "2026-11-01", "2026-11-01", default);
        Assert.Equal(api.KnownSubtotal, structured.GetProperty("knownSubtotal").GetDecimal());
        Assert.DoesNotContain(AppFactory.RefreshToken, json.RootElement.GetRawText());
    }
}
