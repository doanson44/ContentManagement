namespace ContentManagement.Server.Storage;

public sealed record StoredBinary(string StorageKey, long SizeBytes, string Sha256);
