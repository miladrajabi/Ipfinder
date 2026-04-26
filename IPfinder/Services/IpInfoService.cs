using System.Net.Http.Json;

namespace IPfinder.Services;

public sealed class IpInfoService
{
    private static readonly Uri IpAddressEndpoint = new("https://api.ipify.org");
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<IpInfoResult> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var ipAddress = (await Client.GetStringAsync(IpAddressEndpoint, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return new IpInfoResult("Unknown", "Location unavailable.");
        }

        var location = await Client.GetFromJsonAsync<IpLocationResponse>(
            $"https://ipwho.is/{Uri.EscapeDataString(ipAddress)}",
            cancellationToken);

        if (location is not { Success: true })
        {
            return new IpInfoResult(ipAddress, location?.Message ?? "Location unavailable.");
        }

        var flag = string.IsNullOrWhiteSpace(location.Flag?.Emoji) ? string.Empty : $"{location.Flag.Emoji} ";
        var country = string.IsNullOrWhiteSpace(location.Country) ? "Unknown country" : location.Country;
        var city = string.IsNullOrWhiteSpace(location.City) ? "Unknown city" : location.City;
        var isp = string.IsNullOrWhiteSpace(location.Connection?.Isp) ? "Unknown ISP" : location.Connection.Isp;

        return new IpInfoResult(ipAddress, $"{flag}{country} ({city}) - {isp}");
    }
}

public sealed record IpInfoResult(string IpAddress, string LocationText);
