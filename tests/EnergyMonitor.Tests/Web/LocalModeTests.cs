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
    public void DevelopmentSignIn_IsStillRefused_OutsideDevelopment()
    {
        using var app = new AppFactory().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("only allowed in the Development environment", error.ToString());
    }
}
