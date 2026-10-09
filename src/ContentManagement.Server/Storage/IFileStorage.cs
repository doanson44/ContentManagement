namespace ContentManagement.Server.Storage;

public interface IFileStorage
{
    Task<StoredBinary> SaveAsync(Stream content, long maxBytes, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
}
