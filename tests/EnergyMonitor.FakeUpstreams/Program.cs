namespace EnergyMonitor.FakeUpstreams;

// An explicit entry point in a namespace, so its Program type cannot be confused with the application's.
internal static class FakeProgram
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<FakeState>();

        var app = builder.Build();
        app.MapFakeUpstreams();
        app.Run();
    }
}
