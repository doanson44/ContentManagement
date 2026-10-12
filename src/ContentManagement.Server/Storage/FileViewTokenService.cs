using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Storage;

public sealed class FileViewTokenService(IConfiguration configuration, IOptions<ContentManagementOptions> options)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(
        configuration["Authentication:ApiKey"] ?? throw new InvalidOperationException("Authentication:ApiKey must be configured.")));

    public string CreateRelativeUrl(Guid fileId)
    {
        var payload = fileId.ToString("N") + "." + DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var encoded = Encode(Encoding.UTF8.GetBytes(payload));
        var signature = Encode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(encoded)));
        var path = "/api/files/view/" + encoded + "." + signature;
        return string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl) ? path : options.Value.PublicBaseUrl.TrimEnd('/') + path;
    }

    public bool TryValidate(string token, out Guid fileId)
    {
        fileId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Decode(parts[0]);
            signature = Decode(parts[1]);
        }
        catch (FormatException) { return false; }

        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return false;
        var values = Encoding.UTF8.GetString(payloadBytes).Split('.');
        if (values.Length != 2 || !Guid.TryParseExact(values[0], "N", out fileId) ||
            !long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires)
        {
            fileId = Guid.Empty;
            return false;
        }
        return true;
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
