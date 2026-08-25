using System.Net;
using System.Text.Json;

namespace ErpApi.Services;

public interface IGeoLocationService
{
    Task<string> ResolveLocationAsync(string? ip);
}

public class GeoLocationService : IGeoLocationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GeoLocationService> _logger;

    public GeoLocationService(HttpClient httpClient, ILogger<GeoLocationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    private class IpApiResponse
    {
        public string? Status { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
    }

    public async Task<string> ResolveLocationAsync(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "unknown" || IsPrivateOrLoopback(ip))
        {
            return "Yerel ağ";
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = await _httpClient.GetFromJsonAsync<IpApiResponse>(
                $"http://ip-api.com/json/{ip}?fields=status,country,city", cts.Token);

            if (response == null || response.Status != "success")
            {
                return "Bilinmiyor";
            }

            return string.IsNullOrEmpty(response.City)
                ? (response.Country ?? "Bilinmiyor")
                : $"{response.City}, {response.Country}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "IP konum sorgusu başarısız oldu (IP: {Ip})", ip);
            return "Bilinmiyor";
        }
    }

    private static bool IsPrivateOrLoopback(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        // fc00::/7 (IPv6 unique local address)
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && (bytes[0] & 0xfe) == 0xfc;
    }
}
