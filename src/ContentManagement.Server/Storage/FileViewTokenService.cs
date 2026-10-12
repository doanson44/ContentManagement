using System.Security.Cryptography;
using System.Text;
using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Storage;

/// <summary>
/// Creates short-lived, file-specific bearer URLs. The existing server-side API key is the signing
/// secret, so no signing secret is ever sent to the browser or persisted with file metadata.
/// </summary>
public sealed class FileViewTokenService(IConfiguration configuration, IOptions<ContentManagementOptions> options)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        configuration["Authentication:ApiKey"] ?? throw new InvalidOperationException("Authentication:ApiKey must be configured."));

    public string CreateRelativeUrl(Guid fileId)
    {
        var expires = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        var payload = fileId.ToString("N") + "." + expires.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(encodedPayload));
        return options.Value.PublicBaseUrl is { Length: > 0 } baseUrl
            ? baseUrl.TrimEnd('/') + "/api/files/view/" + encodedPayload + "." + Base64UrlEncode(signature)
            : "/api/files/view/" + encodedPayload + "." + Base64UrlEncode(signature);
    }

    public bool TryValidate(string token, out Guid fileId)
    {
        fileId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0] = DecodePayload(parts[0])?.Split('.')[0] ?? "", "N", out fileId))
            return false;

        var payload = DecodePayload(token[..token.LastIndexOf('.')]);
        if (payload is null) { fileId = Guid.Empty; return false; }
        var payloadParts = payload.Split('.');
        if (payloadParts.Length != 2 || !Guid.TryParseExact(payloadParts[0], "N", out fileId) ||
            !long.TryParse(payloadParts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var expires) ||
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires)
        {
            fileId = Guid.Empty;
            return false;
        }

        byte[] suppliedSignature;
        try { suppliedSignature = Base64UrlDecode(parts[2]); }
        catch (FormatException) { fileId = Guid.Empty; return false; }
        var expectedSignature = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(token[..token.LastIndexOf('.')]));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
        {
            fileId = Guid.Empty;
            return false;
        }
        return true;
    }

    private static string? DecodePayload(string value)
    {
        try { return Encoding.UTF8.GetString(Base64UrlDecode(value)); }
        catch (FormatException) { return null; }
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
