namespace ContentManagement.FileApi.Storage;

public sealed class FileAlreadyExistsException : Exception
{
    public FileAlreadyExistsException() : base("A file already exists at the requested filePath.")
    {
    }
}
