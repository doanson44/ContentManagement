using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContentManagement.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ContentManagement.Server.Storage;

/// <summary>Server-to-server client for the standalone ContentManagement.FileApi.</summary>
public sealed class ExternalFileApiClient(
    IHttpClientFactory httpClientFactory,
    ContentManagementDbContext db,
    IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector =
        protectionProvider.CreateProtector("ContentManagement.ExternalFileApi.ApiKey.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StoredBinary> UploadAsync(
        string baseUrl, string protectedApiKey, string filePath, string fileName,
        Stream content, CancellationToken cancellationToken)
    {
        var key = _protector.Unprotect(protectedApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{NormalizeBaseUrl(baseUrl)}/api/files?filePath={Uri.EscapeDataString(filePath)}");
        request.Headers.Add("X-API-Key", key);
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
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Get,
            $"{NormalizeBaseUrl(settings.ExternalStorageBaseUrl)}/api/files/download?filePath={Uri.EscapeDataString(storageKey)}",
            settings.ExternalStorageApiKeyProtected);
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
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Get,
            $"{NormalizeBaseUrl(settings.ExternalStorageBaseUrl)}/api/files/inventory?olderThanUtc={Uri.EscapeDataString(olderThanUtc.ToUniversalTime().ToString("O"))}",
            settings.ExternalStorageApiKeyProtected);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ExternalFileInventoryItem>>(JsonOptions, cancellationToken)
            ?? throw new HttpRequestException("External File API returned an empty inventory response.");
    }

    public async Task DeleteAsync(string baseUrl, string protectedApiKey, string storageKey, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(nameof(ExternalFileApiClient));
        using var request = CreateAuthorizedRequest(HttpMethod.Delete,
            $"{NormalizeBaseUrl(baseUrl)}/api/files?filePath={Uri.EscapeDataString(storageKey)}", protectedApiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteConfiguredFileAsync(string storageKey, CancellationToken cancellationToken)
    {
        var settings = await GetConfiguredSettingsAsync(cancellationToken);
        await DeleteAsync(settings.ExternalStorageBaseUrl, settings.ExternalStorageApiKeyProtected, storageKey, cancellationToken);
    }

    private async Task<ContentManagement.Server.Domain.SystemSettings> GetConfiguredSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.ExternalStorageBaseUrl) ||
            string.IsNullOrWhiteSpace(settings.ExternalStorageApiKeyProtected))
            throw new InvalidOperationException("External File API is not configured.");
        return settings;
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string protectedApiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-API-Key", _protector.Unprotect(protectedApiKey));
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
