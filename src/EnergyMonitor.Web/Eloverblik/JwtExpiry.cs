using System.Buffers.Text;
using System.Text.Json;

namespace EnergyMonitor.Web.Eloverblik;

public static class JwtExpiry
{
    /// <summary>
    /// Reads the <c>exp</c> claim of a JWT without validating it. Used only to know when to renew a token
    /// we hold ourselves; never for trusting a caller.
    /// </summary>
    public static DateTimeOffset? TryRead(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            return payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
