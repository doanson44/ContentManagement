using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentManagement.Server.Configuration;
using Microsoft.Extensions.Options;

namespace ContentManagement.Server.Storage;

/// <summary>Server-to-server client for the standalone ContentManagement.FileApi.</summary>
public sealed class ExternalFileApiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ExternalStorageOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExternalStorageOptions _options = options.Value;

    public async Task<StoredBinary> UploadAsync(
        string filePath, string fileName, Stream content, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{NormalizeBaseUrl(_options.BaseUrl)}/api/files?filePath={Uri.EscapeDataString(filePath)}");
        request.Headers.Add("X-API-Key", _options.ApiKey);
        using var multipart = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(streamContent, "file", Path.GetFileName(fileName));
        request.Content = multipart;

        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ExternalStoredFile>(JsonOptions, cancellationToken)
            ?? throw new HttpRequestException("External File API returned an empty upload response.");
        if (result.SizeBytes <= 0 || string.IsNullOrWhiteSpace(result.Sha256) ||
            !string.Equals(result.FilePath, filePath, StringComparison.Ordinal))
            throw new HttpRequestException("External File API returned invalid file metadata.");
        return new StoredBinary(result.FilePath, result.SizeBytes, result.Sha256);
    }

    public async Task<HttpResponseMessage> GetFileAsync(string storageKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Get,
            $"{NormalizeBaseUrl(_options.BaseUrl)}/api/files/download?filePath={Uri.EscapeDataString(storageKey)}");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException("External binary was not found.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"External File API download failed with status {(int)status}.", null, status);
        }
        return response;
    }

    public async Task<IReadOnlyList<ExternalFileInventoryItem>> ListConfiguredFilesAsync(
        DateTime olderThanUtc, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Get,
            $"{NormalizeBaseUrl(_options.BaseUrl)}/api/files/inventory?olderThanUtc={Uri.EscapeDataString(olderThanUtc.ToUniversalTime().ToString("O"))}");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ExternalFileInventoryItem>>(JsonOptions, cancellationToken)
            ?? throw new HttpRequestException("External File API returned an empty inventory response.");
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Delete,
            $"{NormalizeBaseUrl(_options.BaseUrl)}/api/files?filePath={Uri.EscapeDataString(storageKey)}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    public Task DeleteConfiguredFileAsync(string storageKey, CancellationToken cancellationToken) =>
        DeleteAsync(storageKey, cancellationToken);

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("External File API is not configured in appsettings or environment configuration.");
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-API-Key", _options.ApiKey);
        return request;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("External File API URL must be an absolute HTTP or HTTPS URL.");
        return uri.ToString().TrimEnd('/');
    }

    private sealed record ExternalStoredFile(string FilePath, long SizeBytes, string Sha256);
    public sealed record ExternalFileInventoryItem(string FilePath, DateTime LastWriteTimeUtc, long SizeBytes);
}
