using System.Net;
using System.Net.Http.Json;
using EnergyMonitor.Web.Pricing;
using EnergyMonitor.Core.Abstractions;

namespace EnergyMonitor.Tests.Web;

public sealed class PriceSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task SpotPrices_Retry429_ConvertMWh_AndKeepNegativeDecimals()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Contains("PriceArea", request.RequestUri!.ToString());
            if (++requests == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { records = new[] { new { TimeUTC = "2026-11-01T00:15:00", PriceArea = "DK1", DayAheadPriceDKK = -123.456789m } } }, options: new System.Text.Json.JsonSerializerOptions()) };
        })) { BaseAddress = new Uri("https://fake/") };
        var source = new SpotPriceClient(new Factory(client), new RecordingDelayer());
        var prices = await source.FetchAsync("DK1", new(2026, 11, 1), new(2026, 11, 2), default);
        Assert.Equal(2, requests);
        Assert.Equal(-0.123456789m, prices[0].DkkPerKWh);
        Assert.Equal(TimeSpan.FromMinutes(15), prices[0].EndUtc - prices[0].StartUtc);
    }

    [Fact]
    public async Task SpotSourceFailure_HasSecretFreeErrorCode()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("private source details") })) { BaseAddress = new Uri("https://fake/") };
        var source = new SpotPriceClient(new Factory(client), new RecordingDelayer());
        var error = await Assert.ThrowsAsync<MeasurementSourceException>(() => source.FetchAsync("DK2", new(2026, 11, 1), new(2026, 11, 2), default));
        Assert.Equal("spot_http_502", error.ErrorCode);
        Assert.DoesNotContain("private", error.Message);
    }
}
