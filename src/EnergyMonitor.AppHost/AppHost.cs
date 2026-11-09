var builder = DistributedApplication.CreateBuilder(args);

// Local development: Azurite for Table Storage, and a fake Eloverblik + Telegram so the whole flow can be
// driven without real credentials. Set UseRealUpstreams=true (and the secrets with `dotnet user-secrets`
// on EnergyMonitor.Web) to talk to the real services instead.
var tables = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithLifetime(ContainerLifetime.Persistent))
    .AddTables("tables");

var web = builder.AddProject<Projects.EnergyMonitor_Web>("energymonitor")
    .WithReference(tables)
    .WaitFor(tables)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

if (!string.Equals(builder.Configuration["UseRealUpstreams"], "true", StringComparison.OrdinalIgnoreCase))
{
    var fake = builder.AddProject<Projects.EnergyMonitor_FakeUpstreams>("fake-upstreams");
    var fakeUrl = fake.GetEndpoint("http");
    web.WaitFor(fake)
        .WithEnvironment("Eloverblik__BaseUrl", ReferenceExpression.Create($"{fakeUrl}/customerapi/api/"))
        .WithEnvironment("Eloverblik__RefreshToken", "fake-refresh-token")
        .WithEnvironment("Eloverblik__RetryBaseDelaySeconds", "2")
        .WithEnvironment("Telegram__BaseUrl", fakeUrl)
        .WithEnvironment("Telegram__BotToken", "000000:fake-bot-token")
        .WithEnvironment("Telegram__ChatIds__0", "1000");
}

builder.Build().Run();
