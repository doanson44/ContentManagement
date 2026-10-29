using System.Net;
using System.Net.Sockets;

namespace ContentManagement.Server.Services;

public static class ProxyAddressRules
{
    private static readonly string[] SupportedSchemes = ["http", "https", "socks4", "socks5"];

    public static bool IsSupportedScheme(string scheme) => SupportedSchemes.Contains(scheme, StringComparer.Ordinal);

    public static bool TryNormalize(string? candidate, out string address, out string protocol)
    {
        address = "";
        protocol = "";
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            uri.Port is < 1 or > 65535 ||
            !IsSupportedScheme(uri.Scheme) ||
            !IPAddress.TryParse(uri.Host, out _))
            return false;

        protocol = uri.Scheme;
        address = $"{protocol}://{uri.Host}:{uri.Port}";
        return true;
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] == 168) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) && bytes[0] < 224;
        }

        var bytes6 = ip.GetAddressBytes();
        return !ip.Equals(IPAddress.IPv6Any) && !(bytes6[0] == 0xFC || bytes6[0] == 0xFD) &&
            !(bytes6[0] == 0xFE && (bytes6[1] & 0xC0) == 0x80);
    }
}
